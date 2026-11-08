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

    public static PackageScheduleSummaryResponse Summary(Order order, IReadOnlyCollection<SubscriptionServiceDay> days,
        SubscriptionPause? pause, PackageChangeRequest? request, DateTimeOffset now)
    {
        var today = SubscriptionMenuRules.QatarToday(now);
        var approved = pause?.Status == "CONFIRMED" && pause.ResumeDate > today;
        var pendingPause = request?.Type == "PAUSE" && request.Status == "PENDING_APPROVAL";
        var display = order.Status;
        if (OrderWorkflowPolicy.CanFulfill(order))
            display = approved ? (pause!.PauseStartDate > today ? "PAUSE_SCHEDULED" : "PAUSED") : pendingPause ? "PAUSE_REQUESTED" : "ACTIVE";
        var scheduled = days.Where(d => IsScheduled(order, d)).ToArray();
        return new(display,
            pendingPause ? "PENDING_APPROVAL" : approved ? "APPROVED" : request?.Type == "PAUSE" ? request.Status : null,
            approved ? pause!.PauseStartDate : pendingPause ? request!.PauseStartDate : null,
            approved ? pause!.ResumeDate : pendingPause ? request!.ResumeDate : null,
            order.EndDate,
            order.Status is "CANCELLED" or "REJECTED" or "COMPLETED" ? 0 :
                days.Count(d => d.Status is not ("SKIPPED" or "CANCELLED" or "DELIVERED" or "COLLECTED")),
            scheduled.Where(d => d.ServiceDate >= today).Select(d => (DateOnly?)d.ServiceDate).Min(),
            scheduled.Any(d => d.ServiceDate == today.AddDays(1)), now);
    }
}
