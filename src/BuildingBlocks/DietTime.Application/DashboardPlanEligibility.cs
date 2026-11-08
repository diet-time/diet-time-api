using DietTime.Domain;

namespace DietTime.Application;

public sealed record DashboardPlanDecision(bool Eligible, string Status, string? ExclusionReason, int RemainingServiceDays, string ScheduleSource);

public static class DashboardPlanEligibility
{
    public static DashboardPlanDecision Evaluate(Order order, IReadOnlyCollection<SubscriptionServiceDay> days,
        SubscriptionPause? pause, PackageChangeRequest? request, DateTimeOffset now)
    {
        var today = SubscriptionMenuRules.QatarToday(now);
        var source = days.Count == 0 ? "PURCHASED_WEEKDAYS" : "PERSISTED_SERVICE_DAYS";
        DashboardPlanDecision Excluded(string state, string reason) => new(false, state, reason, 0, source);
        if (order.Status is "CANCELLED" or "REJECTED" or "COMPLETED")
            return Excluded(order.Status, "ORDER_" + order.Status);
        if (order.Status is "PENDING_APPROVAL" or "PENDING")
            return Excluded("PENDING_APPROVAL", "AWAITING_APPROVAL");
        if (order.Status is not ("CONFIRMED" or "ACTIVE" or "PAUSED"))
            return Excluded(order.Status, "ORDER_NOT_APPROVED");
        if (order.EndDate < today) return Excluded("COMPLETED", "SERVICE_PERIOD_ENDED");

        var weekdays = order.DeliveryDays.Select(d => d.DayOfWeek).ToHashSet();
        bool ValidDate(DateOnly date) => date >= today && date >= order.StartDate && date <= order.EndDate &&
            date.DayOfWeek != DayOfWeek.Friday && weekdays.Contains(date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek);
        // Once initialized, service rows (including skipped closures and moved pause dates)
        // are authoritative. Never regenerate missing dates in a managed schedule.
        var remaining = days.Count == 0
            ? SubscriptionMenuRules.ServiceDates(order.StartDate > today ? order.StartDate : today, order.EndDate, weekdays).Count()
            : days.Where(d => d.FulfilledAt == null && ValidDate(d.ServiceDate) &&
                d.Status is not ("SKIPPED" or "CANCELLED" or "DELIVERED" or "COLLECTED" or "DISPLACED"))
                .Select(d => d.ServiceDate).Distinct().Count();
        if (remaining == 0) return Excluded("COMPLETED", "NO_REMAINING_SERVICE_DAYS");

        var approvedPause = pause?.Status == "CONFIRMED" && pause.ResumeDate > today;
        var state = approvedPause && pause!.PauseStartDate <= today || order.Status == "PAUSED" ? "PAUSED" :
            approvedPause ? "PAUSE_SCHEDULED" :
            order.StartDate > today ? "UPCOMING" :
            request?.Type == "PAUSE" && request.Status == "PENDING_APPROVAL" ? "PAUSE_REQUESTED" : "ACTIVE";
        // Visibility is independent of permission to prepare, deliver, or choose meals.
        return new(true, state, null, remaining, source);
    }
}
