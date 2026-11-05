using System.Globalization;
using DietTime.Domain;

namespace DietTime.Application;

public interface IWhatsAppRuntimeConfiguration
{
    bool Enabled { get; }
    string OperationsNumber { get; }
    string NewOrderContentSid { get; }
    string PublicWebhookBaseUrl { get; }
}

public sealed record WhatsAppTemplate(string Name, string ContentSid, IReadOnlyDictionary<string, string> Variables);

public static class WhatsAppTemplateRegistry
{
    public static WhatsAppTemplate Build(OrderNotification message, Order order, string customerName, string phone,
        string meals, string location, IReadOnlyDictionary<string, string> context, OrderWhatsAppTemplateOptions templates,
        IWhatsAppRuntimeConfiguration runtime)
    {
        string Value(string key) => context.GetValueOrDefault(key) ?? "";
        string Date(DateOnly date) => date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
        var mode = order.FulfillmentMethod == FulfillmentMethods.Takeaway ? "Takeaway" : "Home delivery";
        var amount = $"{order.CurrencyCode} {order.TotalAmount.ToString("0.00", CultureInfo.InvariantCulture)}";
        var (name, sid, values) = message.EventCode switch
        {
            "ORDER_RECEIVED" when message.Audience == "STAFF" => ("diet_time_new_order_admin", runtime.NewOrderContentSid,
                new[] { order.OrderNumber, customerName, phone, order.PlanName, order.PlanDurationName, meals, Date(order.StartDate), mode, location, amount }),
            "ORDER_APPROVED" => ("diet_time_order_confirmed", templates.ApprovedContentSid,
                new[] { customerName, order.OrderNumber, order.PlanName, order.PlanDurationName, meals, Date(order.StartDate), mode }),
            "PAYMENT_RECEIVED" => ("diet_time_payment_received", templates.PaymentReceivedContentSid,
                new[] { customerName, order.OrderNumber, order.PlanName, amount, Value("eventDate") }),
            "ORDER_CANCELLED" => ("diet_time_order_cancelled", templates.CancelledContentSid,
                new[] { customerName, order.OrderNumber, order.PlanName, Value("eventDate"), message.Reason ?? "Reason not provided" }),
            "PACKAGE_PAUSED" => ("diet_time_package_paused", templates.PausedContentSid,
                new[] { customerName, order.OrderNumber, order.PlanName, Value("pauseStart"), Value("resumeDate"), Value("remainingDays") }),
            "PACKAGE_RESUMED" => ("diet_time_package_resumed", templates.ResumedContentSid,
                new[] { customerName, order.OrderNumber, order.PlanName, Value("resumeDate"), Value("remainingDays"), Value("endDate") }),
            "MEAL_REMINDER" => ("diet_time_meal_reminder", templates.MealReminderContentSid,
                new[] { customerName, Value("serviceDate"), Value("deadline") }),
            // Preserve optional pre-existing receipt/rejection templates independently of the seven new templates.
            _ => ("legacy_" + message.EventCode.ToLowerInvariant(), templates.ForEvent(message.EventCode),
                new[] { order.OrderNumber, order.PlanName, message.EventCode, message.Reason ?? "No further action is required." })
        };
        if (values.Any(v => string.IsNullOrWhiteSpace(v))) throw new InvalidOperationException("required_notification_data_missing");
        return new(name, sid, values.Select((value, index) => new KeyValuePair<string, string>((index + 1).ToString(CultureInfo.InvariantCulture),
            value.Replace('\r', ' ').Replace('\n', ' ').Trim())).ToDictionary());
    }

    public static string Location(Order order)
    {
        if (order.FulfillmentMethod == FulfillmentMethods.Takeaway)
            return string.IsNullOrWhiteSpace(order.TakeawayBranchName) ? "Address verification required" :
                string.Join(", ", new[] { order.TakeawayBranchName, order.TakeawayBranchAddress }.Where(p => !string.IsNullOrWhiteSpace(p)));
        var parts = new[] {
            Label("Building", order.DeliveryBuildingNo), Label("Unit", order.DeliveryUnitNumber),
            Label("Street", order.DeliveryStreetNo), Label("Zone", order.DeliveryZoneNo), order.DeliveryArea, order.DeliveryDirections
        }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()).ToList();
        var formatted = order.DeliveryFormattedAddress?.Trim();
        if (!string.IsNullOrWhiteSpace(formatted) && !formatted.Equals("Pinned location, Qatar", StringComparison.OrdinalIgnoreCase)) parts.Insert(0, formatted);
        if (order.DeliveryLatitude is >= -90 and <= 90 && order.DeliveryLongitude is >= -180 and <= 180)
            parts.Add(FormattableString.Invariant($"https://www.google.com/maps?q={order.DeliveryLatitude},{order.DeliveryLongitude}"));
        return parts.Count == 0 ? "Address verification required" : string.Join(", ", parts.Distinct(StringComparer.OrdinalIgnoreCase));
    }
    private static string? Label(string name, string? value) => string.IsNullOrWhiteSpace(value) ? null : $"{name} {value.Trim()}";
}
