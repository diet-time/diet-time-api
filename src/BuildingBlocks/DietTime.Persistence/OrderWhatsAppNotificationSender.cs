using DietTime.Application;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DietTime.Persistence;

public sealed class OrderWhatsAppNotificationSender(DietTimeDbContext db, ITwilioWhatsAppService whatsApp,
    IOptions<OrderWhatsAppTemplateOptions> templates, TimeProvider clock,
    ILogger<OrderWhatsAppNotificationSender> logger) : IOrderNotificationSender
{
    public Task SendAsync(Guid orderId, CancellationToken ct) => ProcessAsync(orderId, ct);
    public Task SendPendingAsync(CancellationToken ct) => ProcessAsync(Guid.Empty, ct);

    private async Task ProcessAsync(Guid orderId, CancellationToken ct)
    {
        for (var batch = 0; batch < 20; batch++)
        {
            var now = clock.GetUtcNow();
            OrderNotification? message;
            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                var rows = await db.OrderNotifications.FromSqlInterpolated($"""
                    SELECT * FROM public.order_notifications
                    WHERE ({orderId} = '00000000-0000-0000-0000-000000000000'::uuid OR order_id = {orderId})
                      AND status IN ('PENDING','PROCESSING','WAITING_CONFIGURATION') AND available_at <= {now}
                      AND (locked_until IS NULL OR locked_until <= {now})
                    ORDER BY available_at, id LIMIT 1 FOR UPDATE SKIP LOCKED
                    """).ToListAsync(ct);
                message = rows.SingleOrDefault();
                if (message is null) return;
                message.Status = "PROCESSING"; message.LockedUntil = now.AddMinutes(3); message.Attempts++;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            try
            {
                var order = await db.Orders.AsNoTracking().Include(o => o.Meals).Include(o => o.DeliveryDays).SingleAsync(o => o.Id == message.OrderId, ct);
                var customer = await (from p in db.CustomerProfiles.AsNoTracking() join u in db.Users.AsNoTracking() on p.UserId equals u.Id
                    where p.Id == order.CustomerProfileId select new { Name = p.PreferredName ?? u.UserName ?? "Customer", u.PhoneNumber }).SingleAsync(ct);
                WhatsAppSendResult result;
                if (message.Audience == "STAFF")
                {
                    var codes = await db.MealTypes.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Code, ct);
                    var quantity = order.Meals.Where(m => !MealTypeClassification.IsSnack(codes[m.MealTypeId])).Sum(m => m.Quantity);
                    result = await whatsApp.SendNewOrderNotificationAsync(new(order.Id, order.OrderNumber, customer.Name,
                        customer.PhoneNumber ?? "", order.PlanName, order.PlanDurationName, quantity, order.StartDate,
                        OrderService.FormatDeliveryDays(order), OrderService.FormatDeliveryAddress(order), order.TotalAmount, order.CurrencyCode, "PENDING_APPROVAL"), ct);
                }
                else
                {
                    var sid = templates.Value.ForEvent(message.EventCode);
                    if (string.IsNullOrWhiteSpace(sid))
                    {
                        message.Status = "WAITING_CONFIGURATION"; message.Attempts--; message.LastError = "approved_customer_template_not_configured";
                        message.AvailableAt = now.AddMinutes(5);
                        logger.LogWarning("Customer WhatsApp event retained until an approved template is configured. OrderId={OrderId} Event={Event}", message.OrderId, message.EventCode);
                        continue;
                    }
                    result = await whatsApp.SendTemplateAsync(new(customer.PhoneNumber ?? "", sid,
                        new Dictionary<string, string> { ["1"] = order.OrderNumber,
                            ["2"] = order.PlanName, ["3"] = EventLabel(message.EventCode), ["4"] = message.Reason ?? "No further action is required." }), ct);
                }
                message.MessageSid = result.MessageId;
                message.LastError = result.ErrorCode;
                if (result.Success) { message.Status = "SENT"; message.SentAt = clock.GetUtcNow(); }
                else if (result.ErrorCode == "twilio_whatsapp_disabled")
                { message.Status = "WAITING_CONFIGURATION"; message.Attempts--; message.AvailableAt = now.AddMinutes(5); }
                else { message.Status = message.Attempts < 3 && result.ErrorCode is "timeout" or "http_error" or "429" or "500" or "502" or "503" ? "PENDING" : "FAILED"; message.AvailableAt = now.AddMinutes(2); }
                if (!result.Success) logger.LogWarning("Order WhatsApp event failed. OrderId={OrderId} Event={Event} Code={Code}", message.OrderId, message.EventCode, result.ErrorCode);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                message.Status = message.Attempts < 3 ? "PENDING" : "FAILED";
                message.LastError = "notification_processing_failed"; message.AvailableAt = clock.GetUtcNow().AddMinutes(2);
                logger.LogError(error, "Background order notification failed. OrderId={OrderId}", message.OrderId);
            }
            finally
            {
                if (!ct.IsCancellationRequested) { message.LockedUntil = null; await db.SaveChangesAsync(ct); }
            }
        }
    }
    private static string EventLabel(string code) => code switch
    {
        "ORDER_RECEIVED" => "Pending Confirmation", "ORDER_APPROVED" => "Confirmed", "ORDER_REJECTED" => "Rejected",
        "ORDER_ON_HOLD" => "On Hold", "PAYMENT_RECEIVED" => "Payment Received", _ => "Order Update"
    };
}
