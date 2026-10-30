namespace DietTime.Application;

public interface IOrderNotificationQueue
{
    bool TryEnqueue(Guid orderId);
}

public interface IOrderNotificationSender
{
    Task SendAsync(Guid orderId, CancellationToken cancellationToken);
}

public sealed class WhatsAppSendResult
{
    public bool Success { get; init; }
    public string? MessageId { get; init; }
    public string? Status { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class WhatsAppMessageStatusResult
{
    public bool Success { get; init; }
    public string? MessageId { get; init; }
    public string? Status { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public int? ProviderHttpStatusCode { get; init; }
    public bool DeliveryConfirmed => Status is "delivered" or "read";
}

public static class TwilioMessageSid
{
    public static bool IsValid(string? sid) => System.Text.RegularExpressions.Regex.IsMatch(
        sid ?? "", @"\A(?:SM|MM)[0-9a-fA-F]{32}\z");
}

public interface ITwilioWhatsAppService
{
    Task<WhatsAppMessageStatusResult> GetMessageStatusAsync(string messageSid, CancellationToken cancellationToken = default);
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
