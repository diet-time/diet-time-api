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
    private async Task<(Order Second, TomorrowMenuResponse FirstMenu, TomorrowMenuResponse SecondMenu)> PrepareCalendarOrdersAsync()
    {
        clock.Now = new(2026, 10, 27, 10, 0, 0, TimeSpan.Zero);
        order.StartDate = new(2026, 10, 28);
        order.EndDate = OrderSchedulingRules.CalculateEndDate(order.StartDate, [1,2,3,4,6,7], 12);
        var regular = await db.MealTypes.SingleAsync(t => t.Code == "MEAL");
        var snack = await db.MealTypes.SingleAsync(t => t.Code == "SNACK");
        order.Meals.Single(m => m.MealTypeId == regular.Id).Quantity = 4;
        var second = new Order {
            Id = Guid.NewGuid(), OrderNumber = "ORD-SECOND", IdempotencyKey = Guid.NewGuid().ToString(),
            CustomerProfileId = order.CustomerProfileId, MealPlanTemplateId = order.MealPlanTemplateId, MealPlanPriceId = order.MealPlanPriceId,
            StartDate = order.StartDate, EndDate = order.EndDate, PlanName = "Second Balance", Status = "ACTIVE",
            DeliveryStartTime = order.DeliveryStartTime, CreatedAt = clock.Now, UpdatedAt = clock.Now
        };
        foreach (var weekday in order.DeliveryDays)
            second.DeliveryDays.Add(new OrderDeliveryDay { Id = Guid.NewGuid(), DayOfWeek = weekday.DayOfWeek });
        second.Meals.Add(new OrderMeal { Id = Guid.NewGuid(), MealTypeId = regular.Id, Quantity = 5 });
        second.Meals.Add(new OrderMeal { Id = Guid.NewGuid(), MealTypeId = snack.Id, Quantity = 1 });
        db.Add(second); await db.SaveChangesAsync();
        await Service().InitializeAsync(order, default); await Service().InitializeAsync(second, default);
        return (second, (await Service().GetTomorrowAsync(order.Id, userId, "en", default))!,
            (await Service().GetTomorrowAsync(second.Id, userId, "en", default))!);
    }

    [UpcomingPostgresFact]
    public async Task Calendar_overlapping_orders_return_all_positions_and_confirmation_is_isolated()
    {
        var (second, a, b) = await PrepareCalendarOrdersAsync();
        await using var factory = new ApiFactory(connection, services => services.AddSingleton<TimeProvider>(clock));
        using var client = factory.CreateClient();
        var pathA = $"/api/v1/orders/{order.Id}/upcoming-meals";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(pathA)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Development-User-Id", userId.ToString());
        var rowsA = (await client.GetFromJsonAsync<JsonElement>(pathA)).GetProperty("data");
        var rowsB = (await client.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{second.Id}/upcoming-meals")).GetProperty("data");
        Assert.Equal(12, rowsA.GetArrayLength()); Assert.Equal(12, rowsB.GetArrayLength());
        Assert.Equal(rowsA[0].GetProperty("deliveryDate").GetString(), rowsB[0].GetProperty("deliveryDate").GetString());
        Assert.Equal(order.Id, rowsA[0].GetProperty("orderId").GetGuid());
        Assert.Equal(second.Id, rowsB[0].GetProperty("orderId").GetGuid());
        Assert.Equal(4, rowsA[0].GetProperty("mealCount").GetInt32());
        Assert.Equal(5, rowsB[0].GetProperty("mealCount").GetInt32());
        var firstSaved = await Service().ConfirmAsync(order.Id, a.SubscriptionServiceDayId!.Value, userId, "en", Request(a), default);
        Assert.True(firstSaved.SelectionLocked);
        await using var fresh = Context();
        var secondDay = await fresh.Set<SubscriptionServiceDay>().Include(d => d.Selections).SingleAsync(d => d.Id == b.SubscriptionServiceDayId);
        Assert.False(secondDay.SelectionLocked); Assert.Null(secondDay.CustomerConfirmedAt);
        Assert.Equal(6, secondDay.Selections.Count(s => s.Status != "CANCELLED"));
        foreach (var (id, menu, count) in new[] { (order.Id, a, 4), (second.Id, b, 5) })
        {
            var endpoint = $"/api/v1/orders/{id}/service-days/{menu.SubscriptionServiceDayId}/menu";
            var response = await client.GetFromJsonAsync<JsonElement>(endpoint);
            var data = response.GetProperty("data");
            Assert.Equal(id, data.GetProperty("orderId").GetGuid());
            var selections = data.GetProperty("selectedMeals").EnumerateArray().ToArray();
            var regular = selections.Where(s => s.GetProperty("meal").GetProperty("category").GetString() == "meal").ToArray();
            Assert.Equal(count, regular.Length);
            Assert.Equal(Enumerable.Range(1, count), regular.Select(s => s.GetProperty("selectionNumber").GetInt32()));
            Assert.Single(selections.Where(s => s.GetProperty("meal").GetProperty("category").GetString() == "snack"));
            Assert.False(data.GetProperty("canEdit").GetBoolean());
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/orders/{order.Id}/service-days/{b.SubscriptionServiceDayId}/menu")).StatusCode);
        var wrongDay = await Assert.ThrowsAsync<UpcomingMealException>(() => Service().ConfirmAsync(order.Id, b.SubscriptionServiceDayId!.Value, userId, "en", Request(b), default));
        Assert.Equal(404, wrongDay.StatusCode);
        await Service().ConfirmAsync(second.Id, b.SubscriptionServiceDayId!.Value, userId, "en", Request(b), default);
        clock.Now = a.CutoffAt;
        var report = await new AdminDeliveryCalendarService(db, Options.Create(new MenuRotationOptions()), clock).GetPreparationSummaryAsync(a.DeliveryDate, default);
        Assert.Equal(11, report.MealItemCount);
        Assert.Equal(new[] { order.OrderNumber, second.OrderNumber }.Order(), report.Selections.Select(s => s.OrderReference).Order());
    }

    [UpcomingPostgresFact]
    public async Task Calendar_completed_deliveries_remain_visible_and_cancelled_selections_are_excluded()
    {
        var (_, a, _) = await PrepareCalendarOrdersAsync();
        var day = await db.Set<SubscriptionServiceDay>().Include(d => d.Selections).SingleAsync(d => d.Id == a.SubscriptionServiceDayId);
        var cancelled = day.Selections.Single(s => s.Category == "meal" && s.SelectionNumber == 4);
        cancelled.Status = "CANCELLED"; day.Status = "DELIVERED"; day.SelectionLocked = true;
        order.Status = "COMPLETED"; await db.SaveChangesAsync();
        var days = (await Service().GetDaysAsync(order.Id, userId, default))!;
        var summary = Assert.Single(days.Where(d => d.SubscriptionServiceDayId == day.Id));
        Assert.Equal("delivered", summary.Status); Assert.Equal("DELIVERED", summary.DeliveryStatus);
        Assert.True(summary.SelectionLocked);
        var menu = (await Service().GetMenuAsync(order.Id, day.Id, userId, "en", default))!;
        Assert.Equal(4, menu.SelectedMeals.Count);
        Assert.DoesNotContain(menu.SelectedMeals, s => s.SelectionId == cancelled.Id);
        Assert.Equal(3, menu.SelectedMeals.Count(s => s.Meal.Category == "meal"));
        Assert.Single(menu.SelectedMeals.Where(s => s.Meal.Category == "snack"));
        Assert.False(menu.CanEdit);
    }

    [UpcomingPostgresFact]
    public async Task Calendar_cancelled_orders_and_days_are_not_active_deliveries_and_ownership_is_checked()
    {
        var (second, a, _) = await PrepareCalendarOrdersAsync();
        second.Status = "CANCELLED";
        var day = await db.Set<SubscriptionServiceDay>().SingleAsync(d => d.Id == a.SubscriptionServiceDayId);
        day.Status = "SKIPPED"; await db.SaveChangesAsync();
        Assert.Empty((await Service().GetDaysAsync(second.Id, userId, default))!);
        var remaining = (await Service().GetDaysAsync(order.Id, userId, default))!;
        Assert.Equal(11, remaining.Count); Assert.DoesNotContain(remaining, d => d.SubscriptionServiceDayId == day.Id);
        Assert.Null(await Service().GetDaysAsync(order.Id, Guid.NewGuid(), default));
        Assert.Null(await Service().GetMenuAsync(order.Id, day.Id, Guid.NewGuid(), "en", default));
    }
}
