namespace DietTime.Application;

public interface IWhatsAppService
{
    Task<WhatsAppSendResult> SendNewOrderNotificationAsync(
        NewOrderWhatsAppNotification notification,
        CancellationToken cancellationToken = default);
}

public sealed class NewOrderWhatsAppNotification
{
    public Guid OrderId { get; init; }
    public string OrderNumber { get; init; } = string.Empty;
    public string CustomerName { get; init; } = string.Empty;
    public string CustomerMobile { get; init; } = string.Empty;
    public string MealPlanName { get; init; } = string.Empty;
    public string Duration { get; init; } = string.Empty;
    public int MealsPerDay { get; init; }
    public DateOnly StartDate { get; init; }
    public string DeliveryDays { get; init; } = string.Empty;
    public string DeliveryAddress { get; init; } = string.Empty;
    public decimal TotalAmount { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string OrderStatus { get; init; } = string.Empty;
}

public sealed class WhatsAppSendResult
{
    public bool Success { get; init; }
    public string? MessageId { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}
