using DietTime.Domain;

namespace DietTime.Application;

public static class FulfillmentStaffRoles
{
    public const string Write = "Admin";
    public const string Read = "Admin,Operations";
}

public sealed record ServiceDayProgress(string Status, bool IsOverdue);
public sealed record FulfillmentProgress(int TotalServiceDays, int PendingCount, int OverdueCount, int DeliveredCount, int CollectedCount)
{
    public int FulfilledCount => DeliveredCount + CollectedCount;
    public string Status => TotalServiceDays == 0 ? "NO_DELIVERIES" : OverdueCount > 0 ? "OVERDUE"
        : FulfilledCount == TotalServiceDays ? "FULFILLED" : FulfilledCount > 0 ? "PARTIALLY_FULFILLED" : "SCHEDULED";
}

public static class DailyFulfillmentTracking
{
    private static readonly TimeZoneInfo Qatar = TimeZoneInfo.FindSystemTimeZoneById("Asia/Qatar");
    public static bool IsTrackableStatus(string status) => status is "PLANNED" or "CONFIRMED" or "SCHEDULED" or "DELIVERED" or "COLLECTED";
    public static bool IsEligible(SubscriptionServiceDay day, Order order) => day.OrderId == order.Id &&
        IsTrackableStatus(day.Status) && OrderWorkflowPolicy.CanFulfill(order) && day.ServiceDate.DayOfWeek != DayOfWeek.Friday &&
        AdminDeliveryCalendarScheduling.IsScheduled(day.ServiceDate, order.StartDate, order.EndDate, order.DeliveryDays.Select(d => d.DayOfWeek).ToArray());

    public static DateTimeOffset? Deadline(DateOnly date, Order order, DeliveryTimeSlot? configuredSlot)
    {
        // Takeaway has no collection-window configuration. Delivery slot placeholders must never become collection deadlines.
        if (order.FulfillmentMethod != FulfillmentMethods.HomeDelivery) return null;
        var start = configuredSlot?.StartTime ?? order.DeliveryStartTime;
        var end = configuredSlot?.EndTime ?? order.DeliveryEndTime;
        if (configuredSlot is null && (string.IsNullOrWhiteSpace(order.DeliveryTimeSlotName) || start == end)) return null;
        var deadlineDate = end <= start ? date.AddDays(1) : date;
        var local = deadlineDate.ToDateTime(end, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, Qatar), TimeSpan.Zero);
    }

    public static bool IsOverdue(DateOnly date, string status, DateTimeOffset? deadline, DateTimeOffset now) =>
        status is not ("DELIVERED" or "COLLECTED") && IsTrackableStatus(status) && deadline.HasValue &&
        date <= DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Qatar).DateTime) && now > deadline.Value;

    public static FulfillmentProgress Summarize(IEnumerable<ServiceDayProgress> source)
    {
        var rows = source.ToArray();
        var delivered = rows.Count(r => r.Status == "DELIVERED");
        var collected = rows.Count(r => r.Status == "COLLECTED");
        var overdue = rows.Count(r => r.IsOverdue && r.Status is not ("DELIVERED" or "COLLECTED"));
        return new(rows.Length, rows.Length - delivered - collected - overdue, overdue, delivered, collected);
    }
}
