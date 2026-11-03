using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    private Task EnforceManagedServiceDayConstraintAsync() => db.Database.ExecuteSqlRawAsync("""
        ALTER TABLE public.subscription_service_days ADD CONSTRAINT ck_managed_service_day
        CHECK (order_id IS NULL OR (
            meal_plan_template_id IS NOT NULL AND service_sequence IS NOT NULL AND service_sequence > 0
            AND menu_week_number IS NOT NULL AND menu_week_number BETWEEN 1 AND 4
            AND meal_count IS NOT NULL AND meal_count > 0 AND snack_count IS NOT NULL AND snack_count >= 0
            AND EXTRACT(isodow FROM service_date) <> 5)) NOT VALID;
        """);
    [UpcomingPostgresFact]
    public async Task Pause_24_remaining_days_preserves_deployed_managed_day_constraint()
    {
        // Match the reported November 4 -> December 17 pause with paid entitlement.
        clock.Now = new(2026, 11, 3, 16, 21, 0, TimeSpan.Zero);
        order.StartDate = new(2026, 11, 4);
        order.EndDate = SubscriptionMenuRules.ServiceDates(order.StartDate, new(2026, 12, 31),
            order.DeliveryDays.Select(d => d.DayOfWeek).ToHashSet()).Take(24).Last();
        order.PaymentStatus = "PAID";
        await db.SaveChangesAsync();
        // Exact deployed constraint supplied with PostgreSQL error 23514.
        await EnforceManagedServiceDayConstraintAsync();
        var service = Service();
        await service.InitializeAsync(order, default);
        var before = await db.Set<SubscriptionServiceDay>().AsNoTracking().Where(d => d.OrderId == order.Id).ToArrayAsync();
        var start = new DateOnly(2026, 11, 4);
        var resume = new DateOnly(2026, 12, 17);
        var preview = await service.PreviewPauseAsync(order.Id, userId, new(start, resume, true), default);
        Assert.Equal(24, preview.RemainingServiceDays);
        var command = new PausePackageRequest(Guid.NewGuid(), preview.RowVersion, preview.ScheduleVersion, start, resume, "not in country", true);
        var result = await service.PauseAsync(order.Id, userId, command, default);
        Assert.Equal(24, result.RemainingServiceDays);
        Assert.Equal(resume, result.NextScheduledDelivery);
        await using var fresh = Context();
        var after = await fresh.Set<SubscriptionServiceDay>().AsNoTracking().Where(d => d.OrderId == order.Id).ToArrayAsync();
        Assert.Equal(before.Select(d => d.Id).Order(), after.Select(d => d.Id).Order());
        Assert.All(after, d => Assert.True(d.ServiceDate >= resume));
        Assert.Single(await fresh.Set<SubscriptionPause>().ToArrayAsync());
        Assert.All(after, d => Assert.NotEqual(DayOfWeek.Friday, d.ServiceDate.DayOfWeek));
        await service.PauseAsync(order.Id, userId, command, default);
        var resumed = await service.ResumeAsync(order.Id, userId, new(Guid.NewGuid(), command.CommandId, result.RowVersion), default);
        Assert.Equal(24, resumed.RemainingServiceDays);
        var restored = await fresh.Set<SubscriptionServiceDay>().AsNoTracking().Where(d => d.OrderId == order.Id).ToArrayAsync();
        Assert.Equal(before.Select(d => (d.Id, d.ServiceDate)).OrderBy(d => d.Id), restored.Select(d => (d.Id, d.ServiceDate)).OrderBy(d => d.Id));
        Assert.All(restored, d => Assert.NotEqual(DayOfWeek.Friday, d.ServiceDate.DayOfWeek));
        Assert.Equal("PAID", (await fresh.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).PaymentStatus);

    }
}