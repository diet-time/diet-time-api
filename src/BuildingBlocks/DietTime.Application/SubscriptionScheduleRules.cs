using DietTime.Contracts;
using DietTime.Domain;

namespace DietTime.Application;

public static class SubscriptionScheduleRules
{
    // Selection finalization is independent of whether a delivery still exists.
    public static bool IsScheduled(Order order, SubscriptionServiceDay day) =>
        OrderWorkflowPolicy.CanFulfill(order) && day.Status is "PLANNED" or "CONFIRMED" &&
        day.FulfilledAt == null && day.ServiceDate >= order.StartDate && day.ServiceDate <= order.EndDate &&
        day.ServiceDate.DayOfWeek != DayOfWeek.Friday &&
        order.DeliveryDays.Any(d => d.DayOfWeek == (day.ServiceDate.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)day.ServiceDate.DayOfWeek));

    // A future pause is already effective when all remaining services have been deferred.
    // Keep "scheduled" only while there are real services before the pause starts.
    public static bool PauseInEffect(Order order, IReadOnlyCollection<SubscriptionServiceDay> days,
        SubscriptionPause pause, DateOnly today)
    {
        if (pause.PauseStartDate <= today) return true;
        if (days.Count == 0)
            return !SubscriptionMenuRules.ServiceDates(order.StartDate > today ? order.StartDate : today,
                order.EndDate < pause.PauseStartDate ? order.EndDate : pause.PauseStartDate.AddDays(-1),
                order.DeliveryDays.Select(d => d.DayOfWeek).ToHashSet()).Any();
        return !days.Any(d => d.FulfilledAt == null && d.Status is "PLANNED" or "CONFIRMED" &&
            d.ServiceDate >= today && d.ServiceDate >= order.StartDate && d.ServiceDate <= order.EndDate &&
            d.ServiceDate < pause.PauseStartDate && d.ServiceDate.DayOfWeek != DayOfWeek.Friday &&
            order.DeliveryDays.Any(w => w.DayOfWeek == (d.ServiceDate.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.ServiceDate.DayOfWeek)));
    }

    public static PackageScheduleSummaryResponse Summary(Order order, IReadOnlyCollection<SubscriptionServiceDay> days,
        SubscriptionPause? pause, PackageChangeRequest? request, DateTimeOffset now)
    {
        var today = SubscriptionMenuRules.QatarToday(now);
        var dashboard = DashboardPlanEligibility.Evaluate(order, days, pause, request, now);
        var approved = pause?.Status == "CONFIRMED" && pause.ResumeDate > today;
        var pendingPause = request?.Type == "PAUSE" && request.Status == "PENDING_APPROVAL";
        var display = approved ? dashboard.Status : order.Status;
        if (!approved && OrderWorkflowPolicy.CanFulfill(order))
            display = pendingPause ? "PAUSE_REQUESTED" : "ACTIVE";
        var scheduled = days.Where(d => IsScheduled(order, d)).ToArray();
        var serviceDates = days.Where(d => d.Status is "PLANNED" or "CONFIRMED" or "DELIVERED" or "COLLECTED")
            .Select(d => d.ServiceDate).Distinct().Order().ToArray();
        return new(display,
            pendingPause ? "PENDING_APPROVAL" : approved ? "APPROVED" : request?.Type == "PAUSE" ? request.Status : null,
            approved ? pause!.PauseStartDate : pendingPause ? request!.PauseStartDate : null,
            approved ? pause!.ResumeDate : pendingPause ? request!.ResumeDate : null,
            serviceDates.Length > 0 ? serviceDates[^1] : order.EndDate,
            dashboard.RemainingServiceDays,
            scheduled.Where(d => d.ServiceDate >= today).Select(d => (DateOnly?)d.ServiceDate).Min(),
            scheduled.Any(d => d.ServiceDate == today.AddDays(1)), now)
        {
            ServiceStartDate = serviceDates.Length > 0 ? serviceDates[0] : null,
            ServiceDates = days.Count > 0 ? serviceDates : null,
            DashboardEligible = dashboard.Eligible, DashboardStatus = dashboard.Status,
            DashboardExclusionReason = dashboard.ExclusionReason, DashboardScheduleSource = dashboard.ScheduleSource
        };
    }
}
