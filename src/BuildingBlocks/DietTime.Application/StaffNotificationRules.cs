namespace DietTime.Application;
public static class StaffNotificationRules
{
    public static readonly string[] AdminEvents = ["ORDER_RECEIVED", "ORDER_APPROVED", "ORDER_REJECTED", "ORDER_CANCELLED", "DELIVERY_OVERDUE", "DELIVERED", "COLLECTED"];
    public static string? OrderEvent(string? previous, string status) => previous == status ? null : status switch {
        "PENDING_APPROVAL" or "PENDING" when previous is null => "ORDER_RECEIVED",
        "CONFIRMED" when previous is "PENDING_APPROVAL" or "PENDING" => "ORDER_APPROVED",
        "REJECTED" when previous is not null => "ORDER_REJECTED",
        "CANCELLED" when previous is not null => "ORDER_CANCELLED",
        _ => null
    };
    public static (string Title, string Message) Content(string type, string number, DateOnly? date) => type switch {
        "ORDER_RECEIVED" => ("New order — Pending Confirmation", $"Order {number} is awaiting staff confirmation."),
        "ORDER_APPROVED" => ("Order confirmed", $"Order {number} has been approved."),
        "ORDER_REJECTED" => ("Order rejected", $"Order {number} has been rejected."),
        "ORDER_CANCELLED" => ("Order cancelled", $"Order {number} has been cancelled."),
        "DELIVERY_OVERDUE" => ("Delivery overdue", $"Order {number} for {date:yyyy-MM-dd} has passed its delivery deadline."),
        "DELIVERED" => ("Order delivered", $"Order {number} for {date:yyyy-MM-dd} was marked Delivered."),
        "COLLECTED" => ("Takeaway collected", $"Order {number} for {date:yyyy-MM-dd} was marked Collected."),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
    public static string Target(Guid orderId, Guid? dayId, DateOnly? date) => dayId.HasValue && date.HasValue
        ? $"/operations/delivery-calendar?date={date:yyyy-MM-dd}&serviceDayId={dayId}"
        : $"/orders?orderId={orderId}";
}
