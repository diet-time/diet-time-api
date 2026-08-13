namespace DietTime.Application;

public sealed class WhatsAppSendResult
{
    public bool Success { get; init; }
    public string? MessageId { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public interface ITwilioWhatsAppService
{
    Task<WhatsAppSendResult> SendNewOrderNotificationAsync(
        NewOrderWhatsAppNotification notification,
        CancellationToken cancellationToken = default);

    Task<WhatsAppSendResult> SendTemplateAsync(
        TwilioWhatsAppTemplateMessage message,
        CancellationToken cancellationToken = default);
}

public sealed record NewOrderWhatsAppNotification(
    Guid OrderId,
    string OrderNumber,
    string CustomerName,
    string CustomerMobile,
    string MealPlanName,
    string Duration,
    int MealsPerDay,
    DateOnly StartDate,
    string DeliveryDays,
    string DeliveryAddress,
    decimal TotalAmount,
    string Currency,
    string OrderStatus);

public sealed record TwilioWhatsAppTemplateMessage(
    string To,
    string ContentSid,
    IReadOnlyDictionary<string, string> ContentVariables);
