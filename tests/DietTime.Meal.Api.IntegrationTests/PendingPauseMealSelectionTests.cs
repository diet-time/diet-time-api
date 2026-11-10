using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Pending_pause_selection_blocks_stale_confirmation_and_rejection_restores_active_plan()
    {
        var command = await ApprovalPauseCommand();
        var service = Service();
        var before = (await service.GetTomorrowAsync(order.Id, userId, "en", default))!;
        Assert.True(before.CanConfirm);
        var schedule = await ApprovalSchedule(db);
        await service.RequestPauseAsync(order.Id, userId, command, default);
        var held = (await service.GetTomorrowAsync(order.Id, userId, "en", default))!;
        Assert.True(held.SelectionBlockedByPendingPause);
        Assert.False(held.CanConfirm);
        Assert.False(held.SelectionLocked);
        Assert.True(held.IsScheduledForDelivery);
        Assert.Equal("PAUSE_REQUESTED", held.PackageSchedule!.DisplayStatus);
        Assert.Equal(before.SelectedMenuItemIds, held.SelectedMenuItemIds);
        Assert.Empty(held.AvailableMeals);
        Assert.Empty(held.AvailableSnacks);
        var error = await Assert.ThrowsAsync<UpcomingMealException>(() => service.ConfirmAsync(
            order.Id, before.SubscriptionServiceDayId!.Value, userId, "en", Request(before), default));
        Assert.Equal("pause_request_pending", error.Code);
        Assert.Equal(schedule, await ApprovalSchedule(db));
        Assert.Empty(await db.Set<CustomerMealSelectionHistory>().ToArrayAsync());
        var daily = await service.GetMenuAsync(order.Id, before.SubscriptionServiceDayId!.Value, userId, "en", default);
        Assert.Contains("pause request", daily!.LockReason);
        Assert.Empty(daily.AvailableAlternatives);

        await service.ReviewChangeRequestAsync(command.CommandId, Guid.NewGuid(), new(false, "Pause declined"), default);
        var released = (await service.GetTomorrowAsync(order.Id, userId, "en", default))!;
        Assert.False(released.SelectionBlockedByPendingPause);
        Assert.True(released.CanConfirm);
        Assert.Equal("ACTIVE", released.PackageSchedule!.DisplayStatus);
        Assert.Equal("ACTIVE", (await service.GetPackageAsync(order.Id, userId, default)).DisplayStatus);
        Assert.True(released.IsScheduledForDelivery);
        Assert.Equal(schedule, await ApprovalSchedule(db));
        var confirmed = await service.ConfirmAsync(order.Id, released.SubscriptionServiceDayId!.Value,
            userId, "en", Request(released), default);
        Assert.Equal("CUSTOMER_CONFIRMED", confirmed.SelectionStatus);
    }

    [UpcomingPostgresFact]
    public async Task Pending_pause_selection_only_blocks_dates_from_start_until_exclusive_resume()
    {
        await EnforceManagedServiceDayConstraintAsync();
        var before = await PrepareAsync();
        var service = Service();
        var start = before.DeliveryDate.AddDays(1);
        var resume = start.AddDays(3);
        var preview = await service.PreviewPauseAsync(order.Id, userId, new(start, resume), default);
        await service.RequestPauseAsync(order.Id, userId,
            new(Guid.NewGuid(), preview.RowVersion, preview.ScheduleVersion, start, resume, "Travel"), default);
        var preceding = (await service.GetTomorrowAsync(order.Id, userId, "en", default))!;
        Assert.True(preceding.CanConfirm);
        Assert.False(preceding.SelectionBlockedByPendingPause);

        clock.Now = SubscriptionMenuRules.QatarCutoff(start, 1, new(12, 0));
        var first = (await service.GetTomorrowAsync(order.Id, userId, "en", default))!;
        Assert.Equal(start, first.DeliveryDate);
        Assert.True(first.SelectionBlockedByPendingPause);
        Assert.False(first.CanConfirm);
        clock.Now = SubscriptionMenuRules.QatarCutoff(start.AddDays(1), 1, new(12, 0));
        var inside = (await service.GetTomorrowAsync(order.Id, userId, "en", default))!;
        Assert.True(inside.SelectionBlockedByPendingPause);
        Assert.False(inside.CanConfirm);

        clock.Now = SubscriptionMenuRules.QatarCutoff(resume, 1, new(12, 0));
        var restarting = (await service.GetTomorrowAsync(order.Id, userId, "en", default))!;
        Assert.Equal(resume, restarting.DeliveryDate);
        Assert.False(restarting.SelectionBlockedByPendingPause);
        Assert.True(restarting.CanConfirm);
        Assert.Equal("PENDING_APPROVAL", (await service.GetPackageAsync(order.Id, userId, default)).LatestRequest!.Status);
    }
}
