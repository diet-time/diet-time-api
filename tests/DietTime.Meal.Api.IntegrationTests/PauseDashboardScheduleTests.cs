using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Pause_dashboard_order_summaries_keep_each_packages_actual_schedule_independent()
    {
        var command = await ApprovalPauseCommand();
        var other = (Order)db.Entry(order).CurrentValues.ToObject();
        other.Id = Guid.NewGuid(); other.OrderNumber = "ORD-OTHER"; other.IdempotencyKey = Guid.NewGuid().ToString();
        foreach (var day in order.DeliveryDays)
            other.DeliveryDays.Add(new OrderDeliveryDay { Id = Guid.NewGuid(), DayOfWeek = day.DayOfWeek });
        foreach (var meal in order.Meals)
            other.Meals.Add(new OrderMeal { Id = Guid.NewGuid(), MealTypeId = meal.MealTypeId, Quantity = meal.Quantity });
        db.Add(other); await db.SaveChangesAsync(); await Service().InitializeAsync(other, default);
        await Service().RequestPauseAsync(order.Id, userId, command, default);
        await Service().ReviewChangeRequestAsync(command.CommandId, Guid.NewGuid(), new(true), default);
        var orders = new DietTime.Persistence.OrderService(db, clock,
            Microsoft.Extensions.Options.Options.Create(new DietTime.Persistence.OrderOptions()), new BookingQueue(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DietTime.Persistence.OrderService>.Instance);
        var response = (await orders.GetCustomerOrdersAsync(order.CustomerProfileId, userId, null, 1, 20, default))!;
        var paused = response.Items.Single(item => item.Id == order.Id).PackageSchedule!;
        var active = response.Items.Single(item => item.Id == other.Id).PackageSchedule!;
        Assert.Equal("PAUSE_SCHEDULED", paused.DisplayStatus); Assert.False(paused.TomorrowScheduled);
        Assert.Equal("ACTIVE", active.DisplayStatus); Assert.True(active.TomorrowScheduled);
        Assert.Equal(command.PauseStartDate, paused.PauseStartDate); Assert.Equal(command.ResumeDate, paused.ResumeDate);
        Assert.Equal(12, paused.RemainingServiceDays); Assert.Equal(12, active.RemainingServiceDays);
        Assert.True(paused.RevisedEndDate > active.RevisedEndDate);
        Assert.Equal((await Service().GetPackageAsync(order.Id, userId, default)).NextScheduledDelivery, paused.NextScheduledDelivery);
    }

    [UpcomingPostgresFact]
    public async Task Pause_dashboard_pending_approval_gap_and_resume_follow_saved_schedule_without_duplicates()
    {
        var command = await ApprovalPauseCommand();
        var service = Service();
        var ids = await db.Set<SubscriptionServiceDay>().AsNoTracking().Where(d => d.OrderId == order.Id).OrderBy(d => d.Id).Select(d => d.Id).ToArrayAsync();
        var choices = await db.Set<CustomerMealSelection>().AsNoTracking().Select(s => s.Id).OrderBy(id => id).ToArrayAsync();
        await service.RequestPauseAsync(order.Id, userId, command, default);
        Assert.Equal("PAUSE_REQUESTED", (await service.GetPackageAsync(order.Id, userId, default)).DisplayStatus);
        Assert.True((await service.GetTomorrowAsync(order.Id, userId, "en", default))!.IsScheduledForDelivery);
        await service.ReviewChangeRequestAsync(command.CommandId, Guid.NewGuid(), new(true), default);
        var package = await service.GetPackageAsync(order.Id, userId, default);
        Assert.Equal("PAUSE_SCHEDULED", package.DisplayStatus);
        Assert.Equal("APPROVED", package.PauseApprovalStatus);
        var tomorrow = (await service.GetTomorrowAsync(order.Id, userId, "en", default))!;
        Assert.Equal("NO_DELIVERY", tomorrow.SelectionStatus);
        Assert.False(tomorrow.IsScheduledForDelivery); Assert.Empty(tomorrow.SelectedMeals);
        Assert.NotNull(tomorrow.PackageSchedule); Assert.False(tomorrow.PackageSchedule!.TomorrowScheduled);
        Assert.Equal(package.ExpectedEndDate, tomorrow.PackageSchedule.RevisedEndDate);
        clock.Now = SubscriptionMenuRules.QatarCutoff(command.PauseStartDate, 0, new(10, 0));
        Assert.Equal("PAUSED", (await service.GetPackageAsync(order.Id, userId, default)).DisplayStatus);
        Assert.False((await service.GetTomorrowAsync(order.Id, userId, "en", default))!.IsScheduledForDelivery);
        clock.Now = SubscriptionMenuRules.QatarCutoff(command.ResumeDate.AddDays(-1), 0, new(10, 0));
        var resumed = (await service.GetTomorrowAsync(order.Id, userId, "en", default))!;
        // The requested resume may be a non-service weekday; the saved schedule decides.
        var actual = await db.Set<SubscriptionServiceDay>().AsNoTracking().AnyAsync(d => d.OrderId == order.Id && d.ServiceDate == command.ResumeDate && (d.Status == "PLANNED" || d.Status == "CONFIRMED"));
        Assert.Equal(actual, resumed.IsScheduledForDelivery);
        var after = await db.Set<SubscriptionServiceDay>().AsNoTracking().Where(d => d.OrderId == order.Id).OrderBy(d => d.Id).ToArrayAsync();
        Assert.Equal(ids, after.Select(d => d.Id));
        Assert.Equal(ids.Length, after.Select(d => d.ServiceDate).Distinct().Count());
        Assert.Equal(choices, await db.Set<CustomerMealSelection>().AsNoTracking().Select(s => s.Id).OrderBy(id => id).ToArrayAsync());
        Assert.Equal(12, package.RemainingServiceDays);
        Assert.Equal(after.Max(d => d.ServiceDate), package.ExpectedEndDate);
    }

    [UpcomingPostgresFact]
    public async Task Pause_dashboard_preserves_committed_tomorrow_before_effective_start()
    {
        await PrepareAsync();
        var tomorrow = await db.Set<SubscriptionServiceDay>().Where(d => d.OrderId == order.Id).OrderBy(d => d.ServiceDate).FirstAsync();
        tomorrow.SelectionLocked = true; tomorrow.CustomerConfirmedAt = clock.Now; tomorrow.SelectionStatus = "COMPLETED";
        await db.SaveChangesAsync();
        var service = Service(); var start = (await service.GetPackageAsync(order.Id, userId, default)).EligiblePauseDates.First();
        var preview = await service.PreviewPauseAsync(order.Id, userId, new(start, start.AddDays(7)), default);
        var command = new PausePackageRequest(Guid.NewGuid(), preview.RowVersion, preview.ScheduleVersion, start, start.AddDays(7), "Travel");
        await service.RequestPauseAsync(order.Id, userId, command, default);
        await service.ReviewChangeRequestAsync(command.CommandId, Guid.NewGuid(), new(true), default);
        var menu = (await service.GetTomorrowAsync(order.Id, userId, "en", default))!;
        Assert.True(menu.IsScheduledForDelivery); Assert.Equal(tomorrow.Id, menu.SubscriptionServiceDayId);
        Assert.Equal("CUSTOMER_CONFIRMED", menu.SelectionStatus);
        Assert.Equal("PAUSE_SCHEDULED", menu.PackageSchedule!.DisplayStatus);
    }

    [UpcomingPostgresFact]
    public async Task Pause_dashboard_locked_suspended_day_is_not_a_delivery_or_calendar_entry()
    {
        await PrepareAsync();
        var tomorrow = await db.Set<SubscriptionServiceDay>().Where(d => d.OrderId == order.Id).OrderBy(d => d.ServiceDate).FirstAsync();
        tomorrow.Status = "PAUSED"; tomorrow.SelectionLocked = true; tomorrow.SelectionStatus = "LOCKED";
        await db.SaveChangesAsync();
        var menu = (await Service().GetTomorrowAsync(order.Id, userId, "en", default))!;
        Assert.False(menu.IsScheduledForDelivery); Assert.Equal("NO_DELIVERY", menu.SelectionStatus); Assert.Empty(menu.SelectedMeals);
        Assert.DoesNotContain((await Service().GetDaysAsync(order.Id, userId, default))!, d => d.SubscriptionServiceDayId == tomorrow.Id);
        var calendar = new DietTime.Persistence.AdminDeliveryCalendarService(db,
            Microsoft.Extensions.Options.Options.Create(new MenuRotationOptions()), clock);
        Assert.Empty((await calendar.GetMonthAsync(tomorrow.ServiceDate, tomorrow.ServiceDate, null, null, default)).Days.Single().Orders);
        Assert.Equal(0, (await calendar.GetPreparationSummaryAsync(tomorrow.ServiceDate, default)).OrderCount);
        Assert.True(await db.Set<CustomerMealSelection>().AnyAsync(s => s.SubscriptionServiceDayId == tomorrow.Id));
    }
}
