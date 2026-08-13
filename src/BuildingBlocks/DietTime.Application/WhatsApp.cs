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
    Task<WhatsAppSendResult> SendTemplateAsync(
        TwilioWhatsAppTemplateMessage message,
        CancellationToken cancellationToken = default);
}

public sealed record TwilioWhatsAppTemplateMessage(
    string To,
    string ContentSid,
    IReadOnlyDictionary<string, string> ContentVariables);
