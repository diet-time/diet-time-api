using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Discount_admin_add_update_remove_and_legacy_update_preserves_the_promotion()
    {
        var service = new MealPlanPricingService(db, clock);
        var row = await db.MealPlanPrices.SingleAsync();
        var request = new UpsertMealPlanPricingRequest(row.MealPlanTemplateId, row.PackageCode!, row.PackageOptionId!.Value, 999);
        await service.UpdateAsync(row.Id, request with { DiscountedPrice = 799 }, userId, default);
        var response = Assert.Single(await service.GetAsync(null, null, null, false, default));
        Assert.Equal(999, response.Price); Assert.Equal(799, response.EffectivePrice);
        Assert.Equal(200, response.DiscountAmount); Assert.Equal(20m, response.DiscountPercentage);
        await service.UpdateAsync(row.Id, request, userId, default);
        Assert.Equal(799, row.DiscountedPrice); // Older clients omit the field.
        await service.UpdateAsync(row.Id, request with { DiscountedPrice = 0 }, userId, default);
        Assert.Equal(0, row.DiscountedPrice);
        await service.UpdateAsync(row.Id, request with { DiscountedPrice = null }, userId, default);
        Assert.Null(row.DiscountedPrice); Assert.Equal(999, row.Amount);
        var createdId = await service.CreateAsync(request with { IsActive = false, DiscountedPrice = 499 }, userId, default);
        Assert.Equal(499, (await db.MealPlanPrices.SingleAsync(p => p.Id == createdId)).DiscountedPrice);
        foreach (var invalid in new[] { -1m, 999m, 1000m, 1.001m })
            await Assert.ThrowsAsync<MealConfigurationException>(() => service.UpdateAsync(row.Id, request with { DiscountedPrice = invalid }, userId, default));
        Assert.Null(row.DiscountedPrice);
    }

    [UpcomingPostgresFact]
    public async Task Discount_purchase_options_and_validation_use_effective_price_in_both_languages()
    {
        var row = await db.MealPlanPrices.SingleAsync(); row.Amount = 999; row.DiscountedPrice = 799; await db.SaveChangesAsync();
        var service = new CustomerMealPlanPurchaseService(db, new TestStorage(), clock);
        foreach (var language in new[] { "en", "ar" })
        {
            var options = (await service.GetPurchaseOptionsAsync(order.MealPlanTemplateId.ToString(), userId, language, default))!;
            var package = Assert.Single(Assert.Single(options.MealConfigurations).Packages);
            Assert.Equal(799, package.Amount); Assert.Equal(999, package.RegularPrice);
            Assert.Equal(66.58m, package.PricePerServiceDay); Assert.True(package.HasDiscount);
        }
        var validated = await service.ValidateSelectionAsync(new(order.MealPlanTemplateId, row.Id), default);
        Assert.Equal(MealPlanSelectionValidationStatus.Valid, validated.Status);
        Assert.Equal(799, validated.Selection!.Amount);
    }

    [UpcomingPostgresFact]
    public async Task Discount_database_rejects_invalid_pair_without_changing_regular_price()
    {
        var id = await db.MealPlanPrices.Select(p => p.Id).SingleAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE public.meal_plan_prices SET discounted_price = amount WHERE id = {id}"));
        Assert.Equal("23514", error.SqlState);
        Assert.Null((await db.MealPlanPrices.AsNoTracking().SingleAsync()).DiscountedPrice);
    }

    [UpcomingPostgresFact]
    public async Task Discount_HTTP_order_snapshot_replay_price_change_and_history_are_consistent()
    {
        var price = await db.MealPlanPrices.SingleAsync(); price.Amount = 999; price.DiscountedPrice = 799;
        var address = new CustomerAddress { Id = Guid.NewGuid(), CustomerProfileId = order.CustomerProfileId, AddressType = "HOME", Area = "Doha", IsActive = true };
        var slot = new DeliveryTimeSlot { Id = Guid.NewGuid(), Code = "AM", Name = "Morning", NameAr = "Morning", StartTime = new(9, 0), EndTime = new(11, 0), IsActive = true };
        db.AddRange(address, slot); await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE SEQUENCE IF NOT EXISTS public.order_number_seq");
        await using var factory = new ApiFactory(connection, services => {
            services.AddSingleton<TimeProvider>(clock);
            services.PostConfigure<DietTime.Infrastructure.TwilioWhatsAppOptions>(o => o.Enabled = false);
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Development-User-Id", userId.ToString());
        var configured = await client.GetFromJsonAsync<JsonElement>("/api/admin/meal-plan-prices");
        Assert.Equal(799, configured[0].GetProperty("effectivePrice").GetDecimal());
        Assert.Equal(999, configured[0].GetProperty("regularPrice").GetDecimal());
        var legacy = await client.GetFromJsonAsync<JsonElement>($"/api/v1/admin/meal-plan-pricing/{price.Id}");
        Assert.Equal(799, legacy.GetProperty("data").GetProperty("discountedPrice").GetDecimal());
        var request = new PlaceOrderRequest(order.CustomerProfileId, order.MealPlanTemplateId, price.Id, address.Id, slot.Id,
            order.StartDate, [1,2,3,4,6,7], order.Meals.Select(m => new PlaceOrderMealRequest(m.MealTypeId, m.Quantity)).ToArray(),
            ExpectedTotalAmount: 799, ExpectedRegularPrice: 999, ExpectedDiscountAmount: 200, ExpectedCurrencyCode: "QAR");
        async Task<HttpResponseMessage> Post(string key, PlaceOrderRequest body)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders") { Content = JsonContent.Create(body) };
            message.Headers.Add("Idempotency-Key", key); return await client.SendAsync(message);
        }
        var key = Guid.NewGuid().ToString();
        var first = await Post(key, request);
        Assert.True(first.StatusCode == HttpStatusCode.Created, await first.Content.ReadAsStringAsync());
        var saved = await first.Content.ReadFromJsonAsync<JsonElement>();
        var savedId = saved.GetProperty("id").GetGuid();
        Assert.Equal(999, saved.GetProperty("pricing").GetProperty("subtotal").GetDecimal());
        Assert.Equal(200, saved.GetProperty("pricing").GetProperty("discountAmount").GetDecimal());
        Assert.Equal(799, saved.GetProperty("pricing").GetProperty("totalAmount").GetDecimal());
        price.DiscountedPrice = 699; await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Created, (await Post(key, request)).StatusCode);
        var stale = await Post(Guid.NewGuid().ToString(), request);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("price_changed", (await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(2, await db.Orders.CountAsync()); // Seed order plus one real purchase.
        var changed = await Post(Guid.NewGuid().ToString(), request with { ExpectedTotalAmount = 699, ExpectedDiscountAmount = 300 });
        Assert.Equal(HttpStatusCode.Created, changed.StatusCode);
        price.DiscountedPrice = null; price.Amount = 1299; await db.SaveChangesAsync();
        var history = await client.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{savedId}");
        Assert.Equal(799, history.GetProperty("pricing").GetProperty("totalAmount").GetDecimal());
        Assert.Equal(200, history.GetProperty("pricing").GetProperty("discountAmount").GetDecimal());
        Assert.Equal(999, history.GetProperty("pricing").GetProperty("subtotal").GetDecimal());
    }

    [UpcomingPostgresFact]
    public async Task Discount_migration_is_repeatable_and_preserves_existing_prices_and_orders()
    {
        var original = await db.MealPlanPrices.AsNoTracking().Select(p => p.Amount).SingleAsync();
        // This database was created by this test fixture; no application DB is touched.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE public.meal_plan_prices DROP COLUMN discounted_price CASCADE");
        var script = await File.ReadAllTextAsync(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../database/scripts/20261026_package_discounted_price.sql")));
        await db.Database.ExecuteSqlRawAsync(script);
        await db.Database.ExecuteSqlRawAsync(script);
        var after = await db.MealPlanPrices.AsNoTracking().SingleAsync();
        Assert.Equal(original, after.Amount); Assert.Null(after.DiscountedPrice);
        Assert.Equal(1, await db.Orders.CountAsync());
    }
}
