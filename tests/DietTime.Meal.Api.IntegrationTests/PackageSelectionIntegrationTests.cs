using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Package_sizes_1_through_5_purchase_without_choices_and_confirm_full_menu()
    {
        clock.Now = new(2026, 10, 26, 10, 0, 0, TimeSpan.Zero);
        var slot = new DeliveryTimeSlot { Id = Guid.NewGuid(), Code = "MORNING", Name = "Morning", NameAr = "Morning", StartTime = new(7,0), EndTime = new(11,0), IsActive = true };
        var address = new CustomerAddress { Id = Guid.NewGuid(), CustomerProfileId = order.CustomerProfileId, Area = "Doha", IsActive = true, DeliveryTimeSlotId = slot.Id };
        db.AddRange(slot, address);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE SEQUENCE IF NOT EXISTS public.order_number_seq");
        await using var factory = new ApiFactory(connection, services => {
            services.AddSingleton<TimeProvider>(clock);
            services.PostConfigure<DietTime.Infrastructure.TwilioWhatsAppOptions>(o => o.Enabled = false);
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Development-User-Id", userId.ToString());
        var price = await db.MealPlanPrices.Include(p => p.PackageOption).SingleAsync();
        foreach (var count in Enumerable.Range(1, 5))
        {
            price.MealsPerDay = price.PackageOption!.MealCount = count;
            await db.SaveChangesAsync();
            var body = new PlaceOrderRequest(order.CustomerProfileId, order.MealPlanTemplateId, price.Id, address.Id, slot.Id,
                new(2026,10,27), [1,2,3,4,6,7], count == 1 ? null : []);
            using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders") { Content = JsonContent.Create(body) };
            message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            var purchase = await client.SendAsync(message);
            Assert.True(purchase.StatusCode == HttpStatusCode.Created, await purchase.Content.ReadAsStringAsync());
            var id = (await purchase.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            Assert.False(await db.Set<SubscriptionServiceDay>().AnyAsync(d => d.OrderId == id));
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/admin/orders/{id}/approve", new OrderReviewRequest(1))).StatusCode);
            var days = await db.Set<SubscriptionServiceDay>().AsNoTracking().Where(d => d.OrderId == id).ToArrayAsync();
            Assert.Equal(12, days.Length);
            Assert.All(days, d => { Assert.Equal(count, d.MealCount); Assert.Equal(1, d.SnackCount); Assert.False(d.SelectionLocked); });
            var menu = (await Service().GetTomorrowAsync(id, userId, "en", default))!;
            Assert.True(menu.CanConfirm);
            Assert.Equal(5, menu.AvailableMeals.Count); // All dishes even for a one-meal package.
            Assert.Equal(2, menu.AvailableSnacks.Count);
            Assert.Equal(count + 1, menu.SelectedMeals.Count);
            if (count == 5)
            {
                await db.Set<CustomerMealSelection>().Where(s => s.SubscriptionServiceDayId == menu.SubscriptionServiceDayId).ExecuteDeleteAsync();
                menu = (await Service().GetTomorrowAsync(id, userId, "en", default))!;
                Assert.Empty(menu.SelectedMeals);
            }
            var httpMenu = (await client.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{id}/tomorrow-menu")).GetProperty("data");
            Assert.Equal(5, httpMenu.GetProperty("availableMeals").GetArrayLength());
            Assert.Equal(2, httpMenu.GetProperty("availableSnacks").GetArrayLength());
            if (count == 5) Assert.Empty(httpMenu.GetProperty("selectedMeals").EnumerateArray());
            var choice = new ConfirmMealsRequest(menu.AvailableMeals.Take(count).Select(m => m.MealItemId).ToArray(), [menu.AvailableSnacks.Last().MealItemId]);
            var confirmPath = $"/api/v1/orders/{id}/service-days/{menu.SubscriptionServiceDayId}/confirm-meals";
            var invalid = await client.PostAsJsonAsync(confirmPath, choice with { MealMenuItemIds = choice.MealMenuItemIds.Skip(1).ToArray() });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
            var saved = await client.PostAsJsonAsync(confirmPath, choice);
            Assert.True(saved.StatusCode == HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
            var rows = await db.Set<CustomerMealSelection>().AsNoTracking().Where(s => s.SubscriptionServiceDayId == menu.SubscriptionServiceDayId && s.Status != "CANCELLED").ToArrayAsync();
            Assert.Equal(Enumerable.Range(1, count), rows.Where(s => s.Category == "meal").OrderBy(s => s.SelectionNumber).Select(s => s.SelectionNumber!.Value));
            Assert.Single(rows.Where(s => s.Category == "snack" && s.SelectionNumber == 1));
            Assert.Equal(count + 1, rows.Select(s => s.MenuItemId).Distinct().Count());
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(confirmPath, choice)).StatusCode);
        }
    }

    [UpcomingPostgresFact]
    public async Task Missing_saved_week_uses_saved_templates_actual_cycle()
    {
        var plan = await db.MealPlanTemplates.SingleAsync(); plan.CycleWeeks = 1;
        await db.SaveChangesAsync();
        await Service().InitializeAsync(order, default);
        var day = await db.Set<SubscriptionServiceDay>().SingleAsync(d => d.ServiceSequence == 7);
        day.MenuWeekNumber = null; await db.SaveChangesAsync();
        clock.Now = SubscriptionMenuRules.QatarCutoff(day.ServiceDate, 1, new(12, 0));
        var menu = (await Service().GetTomorrowAsync(order.Id, userId, "en", default))!;
        Assert.Equal(1, menu.MenuWeekNumber); Assert.True(menu.CanConfirm);
        var ids = menu.AvailableMeals.Select(m => m.SlotOptionId).ToArray();
        Assert.All(await db.MealPlanSlotOptions.Where(o => ids.Contains(o.Id)).Select(o => o.Slot.Day.WeekNumber).ToArrayAsync(), week => Assert.Equal(1, week));
    }

    [UpcomingPostgresFact]
    public async Task Empty_menu_does_not_block_purchase_and_service_days_stay_pending()
    {
        clock.Now = new(2026,10,26,10,0,0,TimeSpan.Zero);
        var price = await db.MealPlanPrices.Include(p => p.PackageOption).SingleAsync();
        price.MealsPerDay = price.PackageOption!.MealCount = 5;
        await db.MealPlanSlotOptions.ExecuteUpdateAsync(s => s.SetProperty(o => o.IsAvailable, false));
        var slot = new DeliveryTimeSlot { Id = Guid.NewGuid(), Code = "MORNING", Name = "Morning", NameAr = "Morning", StartTime = new(7,0), EndTime = new(11,0), IsActive = true };
        var address = new CustomerAddress { Id = Guid.NewGuid(), CustomerProfileId = order.CustomerProfileId, Area = "Doha", IsActive = true, DeliveryTimeSlotId = slot.Id };
        db.AddRange(slot, address); await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE SEQUENCE IF NOT EXISTS public.order_number_seq");
        await using var factory = new ApiFactory(connection, services => {
            services.AddSingleton<TimeProvider>(clock);
            services.PostConfigure<DietTime.Infrastructure.TwilioWhatsAppOptions>(o => o.Enabled = false);
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Development-User-Id", userId.ToString());
        // An older client may still send a three-type quantity template. It cannot
        // override the package entitlement or require dishes at purchase time.
        var body = new PlaceOrderRequest(order.CustomerProfileId, order.MealPlanTemplateId, price.Id, address.Id, slot.Id,
            new(2026,10,27), [1,2,3,4,6,7], order.Meals.Select(m => new PlaceOrderMealRequest(m.MealTypeId, m.Quantity)).ToArray());
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders") { Content = JsonContent.Create(body) };
        message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var result = await client.SendAsync(message);
        Assert.True(result.StatusCode == HttpStatusCode.Created, await result.Content.ReadAsStringAsync());
        var id = (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.False(await db.Set<SubscriptionServiceDay>().AnyAsync(d => d.OrderId == id));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/admin/orders/{id}/approve", new OrderReviewRequest(1))).StatusCode);
        var days = await db.Set<SubscriptionServiceDay>().AsNoTracking().Include(d => d.Selections).Where(d => d.OrderId == id).ToArrayAsync();
        Assert.Equal(12, days.Length);
        Assert.All(days, d => { Assert.Equal(5, d.MealCount); Assert.Equal("PENDING", d.SelectionStatus); Assert.NotNull(d.ReviewReason); Assert.False(d.SelectionLocked); Assert.Null(d.AutoFinalizedAt); Assert.Null(d.CustomerConfirmedAt); Assert.Empty(d.Selections); });
    }

    [UpcomingPostgresFact]
    public async Task Admin_warns_and_blocks_publication_when_distinct_dishes_cannot_serve_largest_package()
    {
        var package = await db.MealPackageOptions.SingleAsync(); package.MealCount = 5;
        var mealType = await db.MealTypes.SingleAsync(t => t.Code == "MEAL");
        var snackType = await db.MealTypes.SingleAsync(t => t.Code == "SNACK");
        var plan = await db.MealPlanTemplates.Include(p => p.Days).ThenInclude(d => d.Slots).ThenInclude(s => s.Options).SingleAsync();
        var removed = plan.Days.First().Slots.Single(s => s.MealTypeId == mealType.Id).Options.Last().MealItemId;
        await db.MealItems.Where(m => m.Id == removed).ExecuteUpdateAsync(s => s.SetProperty(m => m.IsAvailable, false));
        await db.SaveChangesAsync();
        await using var factory = new ApiFactory(connection, services => services.AddSingleton<TimeProvider>(clock));
        using var scope = factory.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<IAdminMealService>();
        var validation = await admin.ValidateMealPlanMenuAsync(plan.Id, default);
        Assert.All(validation!.Weeks, week => Assert.Contains(week.Errors, e => e.Code == "INSUFFICIENT_PACKAGE_OPTIONS"));
        var source = plan.Days.First();
        var day = new UpsertPlanDayRequest(source.MenuWeekday, 1, true, source.Slots.Select(s => new UpsertPlanSlotRequest(s.MealTypeId, 1, 1, 5, true, null, false,
            s.Options.Select(o => new UpsertPlanOptionRequest(o.MealItemId, 0, false, true, 1)).ToArray())).ToArray());
        var request = new CreatePlanRequest("INVALID_CAPACITY", "STANDARD", 12, true, null, null,
            [new AdminTranslationRequest("en", "Invalid", null, null)], [day], true, 1);
        var error = await Assert.ThrowsAsync<TemplateDayException>(() => admin.CreatePlanAsync(request, userId, default));
        Assert.Equal("INSUFFICIENT_PACKAGE_OPTIONS", error.Code);
    }
}
