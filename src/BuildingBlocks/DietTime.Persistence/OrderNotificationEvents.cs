using DietTime.Domain;

namespace DietTime.Persistence;

internal static class OrderNotificationEvents
{
    public static void Add(DietTimeDbContext db, Order order, OrderStatusHistory history,
        string eventCode, DateTimeOffset now, bool staff = false)
    {
        foreach (var audience in staff ? new[] { "STAFF", "CUSTOMER" } : new[] { "CUSTOMER" })
            db.OrderNotifications.Add(new OrderNotification
            {
                Id = Guid.NewGuid(), OrderId = order.Id, HistoryId = history.Id, EventCode = eventCode,
                Audience = audience, Reason = eventCode switch
                {
                    "ORDER_RECEIVED" => "Your order has been received and is awaiting confirmation from Diet Time.",
                    "ORDER_APPROVED" => "Diet Time has confirmed your order.",
                    "PAYMENT_RECEIVED" => "Diet Time has recorded your payment.",
                    _ => history.Notes
                }, AvailableAt = now, CreatedAt = now
            });
    }
}
