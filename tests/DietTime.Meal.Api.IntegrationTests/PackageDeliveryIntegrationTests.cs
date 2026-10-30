using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Delivery_migration_seeds_once_and_preserves_order_snapshots()
    {
        db.AddRange(
            new MealPlanPricePackage { Code = "SIX", NameEn = "6 Days", NameAr = "6 Days", DurationDays = 6 },
            new MealPlanPricePackage { Code = "TWENTYFOUR", NameEn = "24 Days", NameAr = "24 Days", DurationDays = 24 });
        order.CustomerAddressId = Guid.NewGuid();
        order.DeliveryTimeSlotId = Guid.NewGuid();
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE public.meal_plan_price_packages DROP COLUMN is_delivery_enabled; ALTER TABLE public.orders DROP COLUMN fulfillment_method");
        var migration = new DietTime.Persistence.Migrations.EnablePackageDelivery();
        var migrationSql = migration.UpOperations
            .OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>().Single().Sql;
        var sql = "BEGIN;\n" + migrationSql + "\nCOMMIT;";
        await db.Database.ExecuteSqlRawAsync(sql);
        db.ChangeTracker.Clear();
        var packages = await db.MealPlanPricePackages.OrderBy(x => x.DurationDays).ToArrayAsync();
        Assert.False(packages[0].IsDeliveryEnabled);
        Assert.True(packages[1].IsDeliveryEnabled);
        Assert.True(packages[2].IsDeliveryEnabled);
        packages[1].IsDeliveryEnabled = false; // An administrator's subsequent choice survives a rerun.
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync(sql);
        Assert.False(await db.MealPlanPricePackages.AsNoTracking().Where(x => x.Code == "FORTNIGHT").Select(x => x.IsDeliveryEnabled).SingleAsync());
        var original = await db.Orders.AsNoTracking().SingleAsync();
        Assert.Equal(OrderStatuses.Confirmed, original.Status);
        Assert.Equal(FulfillmentMethods.HomeDelivery, original.FulfillmentMethod);
        Assert.Equal(order.CustomerAddressId, original.CustomerAddressId);
    }

    [UpcomingPostgresFact]
    public async Task Duration_create_update_and_customer_options_roundtrip_delivery_flag()
    {
        await using var factory = new ApiFactory(connection);
        using var scope = factory.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<IAdminMealService>();
        var request = new UpsertMealPlanPricePackageRequest("NINE", "9 Days", "9 Days", 9, 0, true, false);
        var created = await admin.CreateMealPlanPricePackageAsync(request, userId, default);
        Assert.Equal(MealPlanPricePackageWriteResult.Success, created.Result);
        Assert.False((await admin.GetMealPlanPricePackageAsync("NINE", default))!.IsDeliveryEnabled);
        Assert.Equal(MealPlanPricePackageWriteResult.Success,
            await admin.UpdateMealPlanPricePackageAsync("NINE", request with { IsDeliveryEnabled = true }, userId, default));
        Assert.True((await admin.GetMealPlanPricePackageAsync("NINE", default))!.IsDeliveryEnabled);
        Assert.True((await admin.GetMealPlanPricePackageLookupAsync(default)).Single(x => x.Code == "NINE").IsDeliveryEnabled);
        Assert.True((await admin.GetMealPlanPricePackagesAsync(null, null, null, null, 1, 20, default)).Items.Single(x => x.Code == "NINE").IsDeliveryEnabled);
        var duration = await db.MealPlanPricePackages.SingleAsync(x => x.Code == "FORTNIGHT");
        duration.IsDeliveryEnabled = false;
        await db.SaveChangesAsync();
        var customer = scope.ServiceProvider.GetRequiredService<ICustomerMealPlanPurchaseService>();
        var options = await customer.GetPurchaseOptionsAsync("BALANCE", userId, "en", default);
        Assert.NotNull(options);
        Assert.All(options!.MealConfigurations.SelectMany(x => x.Packages), x => Assert.False(x.IsDeliveryEnabled));
    }

    [UpcomingPostgresFact]
    public async Task Package_delivery_is_authoritative_and_fulfillment_is_snapshotted()
    {
        clock.Now = new(2026, 10, 26, 10, 0, 0, TimeSpan.Zero);
        var slot = new DeliveryTimeSlot { Id = Guid.NewGuid(), Code = "AM", Name = "Morning", NameAr = "Morning", StartTime = new(7, 0), EndTime = new(11, 0), IsActive = true };
        var address = new CustomerAddress { Id = Guid.NewGuid(), CustomerProfileId = order.CustomerProfileId, AddressType = "HOME", Area = "Doha", IsActive = true };
        db.AddRange(slot, address);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE SEQUENCE IF NOT EXISTS public.order_number_seq");
        await using var factory = new ApiFactory(connection, services => {
            services.AddSingleton<TimeProvider>(clock);
            services.PostConfigure<DietTime.Infrastructure.TwilioWhatsAppOptions>(o => o.Enabled = false);
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Development-User-Id", userId.ToString());
        var price = await db.MealPlanPrices.SingleAsync();
        var duration = await db.MealPlanPricePackages.SingleAsync();
        var body = new PlaceOrderRequest(order.CustomerProfileId, order.MealPlanTemplateId, price.Id,
            address.Id, slot.Id, new(2026, 10, 27), [1, 2, 3, 4, 6, 7]);

        async Task<HttpResponseMessage> Submit(PlaceOrderRequest request, string? key = null)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders") { Content = JsonContent.Create(request) };
            message.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
            return await client.SendAsync(message);
        }

        var delivery = await Submit(body);
        Assert.Equal(HttpStatusCode.Created, delivery.StatusCode);
        var deliveryJson = await delivery.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("HOME_DELIVERY", deliveryJson.GetProperty("fulfillmentMethod").GetString());
        var deliveryId = deliveryJson.GetProperty("id").GetGuid();

        duration.IsDeliveryEnabled = false;
        await db.SaveChangesAsync();
        var invalid = await Submit(body);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("delivery_disabled", (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var takeawayKey = Guid.NewGuid().ToString();
        var takeawayRequest = body with { FulfillmentMethod = "TAKEAWAY", CustomerAddressId = null, DeliveryTimeSlotId = null };
        var takeaway = await Submit(takeawayRequest, takeawayKey);
        Assert.Equal(HttpStatusCode.Created, takeaway.StatusCode);
        var takeawayJson = await takeaway.Content.ReadFromJsonAsync<JsonElement>();
        var takeawayId = takeawayJson.GetProperty("id").GetGuid();
        Assert.Equal("TAKEAWAY", takeawayJson.GetProperty("fulfillmentMethod").GetString());
        Assert.False(takeawayJson.GetProperty("delivery").TryGetProperty("address", out var takeawayAddress) && takeawayAddress.ValueKind != JsonValueKind.Null);
        var snapshot = await db.Orders.AsNoTracking().SingleAsync(x => x.Id == takeawayId);
        Assert.Null(snapshot.CustomerAddressId);
        Assert.Null(snapshot.DeliveryTimeSlotId);
        Assert.Equal(OrderStatuses.Confirmed, snapshot.Status);
        var original = await client.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{deliveryId}");
        Assert.Equal("HOME_DELIVERY", original.GetProperty("fulfillmentMethod").GetString());
        Assert.Equal(address.Id, original.GetProperty("delivery").GetProperty("address").GetProperty("addressId").GetGuid());

        duration.IsDeliveryEnabled = true;
        await db.SaveChangesAsync();
        var replay = await Submit(takeawayRequest, takeawayKey);
        Assert.Equal(takeawayId, (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Created, (await Submit(body)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Submit(takeawayRequest)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Submit(body with { FulfillmentMethod = "INVALID" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Submit(body with { CustomerAddressId = null })).StatusCode);

        var summaries = await client.GetFromJsonAsync<JsonElement>($"/api/v1/customer-profiles/{order.CustomerProfileId}/orders");
        Assert.Contains(summaries.GetProperty("items").EnumerateArray(), x => x.GetProperty("id").GetGuid() == takeawayId && x.GetProperty("fulfillmentMethod").GetString() == "TAKEAWAY");
        var dashboard = new OperationsDashboardService(db, clock, Options.Create(new OperationsDashboardOptions()));
        var dispatch = await dashboard.GetDeliveriesAsync(new(2026, 10, 27), 1, 100, default);
        Assert.DoesNotContain(dispatch.Items, x => x.FulfillmentMethod == "TAKEAWAY");
        Assert.Contains(dispatch.Items, x => x.OrderId == deliveryId);
        var preparation = await new AdminDeliveryCalendarService(db, Options.Create(new MenuRotationOptions()), clock)
            .GetPreparationSummaryAsync(new(2026, 10, 27), default);
        Assert.Equal(5, preparation.OrderCount); // Seed order and four successfully purchased orders.
    }
}
