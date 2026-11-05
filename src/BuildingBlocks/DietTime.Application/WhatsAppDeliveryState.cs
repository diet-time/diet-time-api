using DietTime.Domain;
namespace DietTime.Application;
public static class WhatsAppDeliveryState
{
    private static int Rank(string? value) => value switch { "accepted" or "queued" => 1, "sending" => 2, "sent" => 3, "delivered" => 4, "read" => 5, _ => 0 };
    public static void Apply(OrderNotification notification, string status, DateTimeOffset now, string? error = null)
    {
        if (status is "failed" or "undelivered") {
            if (notification.DeliveredAt != null || notification.ReadAt != null) return;
            notification.Status = "FAILED"; notification.ProviderStatus = status; notification.LastError = error;
            return;
        }
        if (Rank(status) == 0 || Rank(status) < Rank(notification.ProviderStatus)) return;
        if (notification.Status == "FAILED" && Rank(status) < 4) return;
        notification.ProviderStatus = status; notification.LastError = null;
        notification.Status = status switch { "read" => "READ", "delivered" => "DELIVERED", _ => "SENT" };
        notification.SentAt ??= now;
        if (status is "delivered" or "read") notification.DeliveredAt ??= now;
        if (status == "read") notification.ReadAt ??= now;
    }
}
