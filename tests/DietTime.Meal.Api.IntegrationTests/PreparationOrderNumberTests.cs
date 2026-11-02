using DietTime.Application;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Preparation_warnings_use_order_numbers_for_reviews_legacy_locks_and_missing_service_days()
    {
        var menu = await PrepareAsync();
        var day = await db.Set<SubscriptionServiceDay>().SingleAsync(d => d.Id == menu.SubscriptionServiceDayId);
        var calendar = new AdminDeliveryCalendarService(db, Options.Create(new MenuRotationOptions()), clock);
        const string reason = "This delivery day requires 5 distinct eligible meal types, but only 4 could be assigned.";
        day.ReviewReason = reason;
        await db.SaveChangesAsync();
        var review = await calendar.GetPreparationSummaryAsync(menu.DeliveryDate, default);
        Assert.Contains($"Order {order.OrderNumber}: {reason}", review.OperationalExceptions);
        Assert.All(review.OperationalExceptions, warning => Assert.DoesNotContain(order.Id.ToString(), warning));

        day.ReviewReason = null; day.SelectionLocked = true; day.SelectionStatus = "LOCKED";
        day.CustomerConfirmedAt = null; day.AutoFinalizedAt = null;
        await db.SaveChangesAsync();
        var legacy = await calendar.GetPreparationSummaryAsync(menu.DeliveryDate, default);
        Assert.Contains(legacy.OperationalExceptions, warning => warning.StartsWith($"Order {order.OrderNumber}: legacy lock"));
        Assert.All(legacy.OperationalExceptions, warning => Assert.DoesNotContain(order.Id.ToString(), warning));

        var missing = new Order
        {
            Id = Guid.NewGuid(), OrderNumber = "ORD-NO-SERVICE", IdempotencyKey = Guid.NewGuid().ToString(),
            CustomerProfileId = order.CustomerProfileId, MealPlanTemplateId = order.MealPlanTemplateId, MealPlanPriceId = order.MealPlanPriceId,
            PlanName = "Balance", PlanDurationName = "1 day", Status = "CONFIRMED", PaymentStatus = "PENDING",
            StartDate = menu.DeliveryDate, EndDate = menu.DeliveryDate, PlacedAt = clock.Now, CreatedAt = clock.Now, UpdatedAt = clock.Now
        };
        missing.DeliveryDays.Add(new OrderDeliveryDay { Id = Guid.NewGuid(), OrderId = missing.Id, DayOfWeek = (int)menu.DeliveryDate.DayOfWeek });
        db.Orders.Add(missing); await db.SaveChangesAsync();
        var uninitialized = await calendar.GetPreparationSummaryAsync(menu.DeliveryDate, default);
        Assert.Contains("Order ORD-NO-SERVICE: service days have not been initialized; review required.", uninitialized.OperationalExceptions);
        Assert.All(uninitialized.OperationalExceptions, warning => { Assert.DoesNotContain(order.Id.ToString(), warning); Assert.DoesNotContain(missing.Id.ToString(), warning); });
        missing.OrderNumber = ""; await db.SaveChangesAsync();
        var unknown = await calendar.GetPreparationSummaryAsync(menu.DeliveryDate, default);
        Assert.Contains(unknown.OperationalExceptions, warning => warning.StartsWith("Order number not recorded:"));
        Assert.All(unknown.OperationalExceptions, warning => Assert.DoesNotContain(missing.Id.ToString(), warning));
        Assert.Equal(reason, review.Selections.Single().ReviewReason);
    }
}
