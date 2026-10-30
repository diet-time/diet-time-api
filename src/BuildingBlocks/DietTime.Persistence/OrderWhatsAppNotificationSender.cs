using DietTime.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DietTime.Persistence;

public sealed class OrderWhatsAppNotificationSender(
    DietTimeDbContext db,
    ITwilioWhatsAppService whatsApp,
    ILogger<OrderWhatsAppNotificationSender> logger) : IOrderNotificationSender
{
    public async Task SendAsync(
        Guid orderId,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("Dispatching WhatsApp new-order notification after commit. OrderId={OrderId}", orderId);
            var order = await db.Orders.AsNoTracking()
                .Include(x => x.DeliveryDays)
                .SingleAsync(x => x.Id == orderId, cancellationToken);
            var customer = await (
                from profile in db.CustomerProfiles.AsNoTracking()
                join user in db.Users.AsNoTracking() on profile.UserId equals user.Id
                where profile.Id == order.CustomerProfileId
                select new { profile.PreferredName, user.UserName, user.PhoneNumber })
                .SingleAsync(cancellationToken);
            var mealsPerDay = await db.MealPlanPrices.AsNoTracking()
                .Where(x => x.Id == order.MealPlanPriceId)
                .Select(x => x.MealsPerDay)
                .SingleAsync(cancellationToken);

            var notification = new NewOrderWhatsAppNotification(
                order.Id,
                order.OrderNumber,
                FirstNonEmpty(customer.PreferredName, customer.UserName),
                customer.PhoneNumber?.Trim() ?? string.Empty,
                order.PlanName,
                order.PlanDurationName,
                mealsPerDay,
                order.StartDate,
                OrderService.FormatDeliveryDays(order),
                OrderService.FormatDeliveryAddress(order),
                order.TotalAmount,
                order.CurrencyCode,
                order.Status);
            var result = await whatsApp.SendNewOrderNotificationAsync(
                notification, cancellationToken);

            if (result.Success)
                logger.LogInformation(
                    "WhatsApp new-order notification accepted by Twilio. OrderId={OrderId} OrderNumber={OrderNumber} WhatsAppMessageId={MessageId} ProviderStatus={ProviderStatus}",
                    order.Id, order.OrderNumber, result.MessageId, result.Status ?? "unknown");
            else if (result.ErrorCode == "twilio_whatsapp_disabled")
                logger.LogInformation("WhatsApp new-order notification skipped. OrderId={OrderId} Reason=twilio_whatsapp_disabled", order.Id);
            else
                logger.LogError(
                    "Failed to send WhatsApp new-order notification. OrderId={OrderId} OrderNumber={OrderNumber} ErrorCode={ErrorCode}",
                    order.Id, order.OrderNumber, result.ErrorCode);
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Failed to send WhatsApp new-order notification after commit. OrderId={OrderId}",
                orderId);
        }
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
