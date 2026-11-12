namespace DietTime.Application;
public static class StaffNotificationRules
{
    public static readonly string[] AdminEvents = ["ORDER_RECEIVED", "ORDER_APPROVED", "ORDER_REJECTED", "ORDER_CANCELLED", "DELIVERY_OVERDUE", "DELIVERED", "COLLECTED",
        "PACKAGE_PAUSE_REQUESTED", "PACKAGE_RESUME_REQUESTED", "PACKAGE_PAUSE_APPROVED", "PACKAGE_RESUME_APPROVED",
        "PACKAGE_PAUSE_REJECTED", "PACKAGE_RESUME_REJECTED"];
    public static string? OrderEvent(string? previous, string status) => previous == status ? null : status switch {
        "PENDING_APPROVAL" or "PENDING" when previous is null => "ORDER_RECEIVED",
        "CONFIRMED" when previous is "PENDING_APPROVAL" or "PENDING" => "ORDER_APPROVED",
        "REJECTED" when previous is not null => "ORDER_REJECTED",
        "CANCELLED" when previous is not null => "ORDER_CANCELLED",
        _ => null
    };
    public static (string Title, string Message) Content(string type, string number, DateOnly? date, string? customerName = null)
    {
        var name = string.IsNullOrWhiteSpace(customerName) ? "the customer" : customerName.Trim();
        return type switch {
            "PACKAGE_PAUSE_REQUESTED" => ("Pause requested", $"{name} requested a pause for order {number}. Awaiting confirmation."),
            "PACKAGE_RESUME_REQUESTED" => ("Resume requested", $"{name} requested to resume order {number}. Awaiting confirmation."),
            "PACKAGE_PAUSE_APPROVED" => ("Pause approved", $"The pause request for {name}, order {number}, was approved."),
            "PACKAGE_RESUME_APPROVED" => ("Resume approved", $"The resume request for {name}, order {number}, was approved."),
            "PACKAGE_PAUSE_REJECTED" => ("Pause rejected", $"The pause request for {name}, order {number}, was rejected."),
            "PACKAGE_RESUME_REJECTED" => ("Resume rejected", $"The resume request for {name}, order {number}, was rejected."),
            "ORDER_RECEIVED" => ("New order received", $"{(name == "the customer" ? "A customer" : name)} placed order {number}. Awaiting confirmation."),
            "ORDER_APPROVED" => ("Order confirmed", $"Order {number} for {name} has been confirmed."),
            "ORDER_REJECTED" => ("Order rejected", $"Order {number} for {name} has been rejected."),
            "ORDER_CANCELLED" => ("Order cancelled", $"Order {number} for {name} has been cancelled."),
            "DELIVERY_OVERDUE" => ("Delivery overdue", $"Delivery for {name}, order {number} on {date:yyyy-MM-dd}, is overdue."),
            "DELIVERED" => ("Order delivered", $"Order {number} for {name} on {date:yyyy-MM-dd} was delivered."),
            "COLLECTED" => ("Takeaway collected", $"Order {number} for {name} on {date:yyyy-MM-dd} was collected."),
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
    }
    public static string Target(Guid orderId, Guid? dayId, DateOnly? date) => dayId.HasValue && date.HasValue
        ? $"/operations/delivery-calendar?date={date:yyyy-MM-dd}&serviceDayId={dayId}"
        : $"/orders?orderId={orderId}";
}
