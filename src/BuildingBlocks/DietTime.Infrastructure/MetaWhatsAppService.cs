using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DietTime.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DietTime.Infrastructure;

public sealed class WhatsAppOptions
{
    public const string SectionName = "WhatsApp";

    public bool Enabled { get; set; }
    public string ApiVersion { get; set; } = "v22.0";
    public string PhoneNumberId { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string OperationsNumber { get; set; } = string.Empty;
    public string NewOrderTemplateName { get; set; } = "new_order_summary";
    public string TemplateLanguage { get; set; } = "en";

    public bool IsValid() => !Enabled ||
        (!string.IsNullOrWhiteSpace(ApiVersion) &&
         !string.IsNullOrWhiteSpace(PhoneNumberId) &&
         !string.IsNullOrWhiteSpace(AccessToken) &&
         WhatsAppPhoneNumber.Normalize(OperationsNumber).Length is >= 7 and <= 15 &&
         !string.IsNullOrWhiteSpace(NewOrderTemplateName) &&
         !string.IsNullOrWhiteSpace(TemplateLanguage));
}

public static class WhatsAppPhoneNumber
{
    public static string Normalize(string? value) =>
        new((value ?? string.Empty).Where(char.IsDigit).ToArray());
}

public sealed class MetaWhatsAppService(
    HttpClient httpClient,
    IOptions<WhatsAppOptions> options,
    ILogger<MetaWhatsAppService> logger) : IWhatsAppService
{
    private const int MaximumAttempts = 3;

    public async Task<WhatsAppSendResult> SendNewOrderNotificationAsync(
        NewOrderWhatsAppNotification notification,
        CancellationToken cancellationToken = default)
    {
        var configuration = options.Value;
        if (!configuration.Enabled)
            return Failure("whatsapp_disabled", "WhatsApp notifications are disabled.");

        var destination = WhatsAppPhoneNumber.Normalize(configuration.OperationsNumber);
        var endpoint = $"{configuration.ApiVersion.Trim().TrimEnd('/')}/" +
            $"{configuration.PhoneNumberId.Trim()}/messages";
        var payload = BuildPayload(notification, configuration, destination);

        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = JsonContent.Create(payload)
                };
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer", configuration.AccessToken.Trim());

                using var response = await httpClient.SendAsync(request, cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode)
                    return new WhatsAppSendResult
                    {
                        Success = true,
                        MessageId = ReadMessageId(responseBody)
                    };

                var failure = ReadMetaFailure(response.StatusCode, responseBody);
                if (!IsTransient(response.StatusCode) || attempt == MaximumAttempts)
                    return failure;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt == MaximumAttempts)
                    return Failure("timeout", "The Meta WhatsApp request timed out.");
            }
            catch (HttpRequestException exception)
            {
                if (attempt == MaximumAttempts)
                    return Failure("http_error", exception.Message);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken);
        }

        logger.LogWarning("Meta WhatsApp send ended without a provider result.");
        return Failure("unknown_error", "Meta WhatsApp did not return a result.");
    }

    internal static object BuildPayload(
        NewOrderWhatsAppNotification notification,
        WhatsAppOptions configuration,
        string destination) => new
    {
        messaging_product = "whatsapp",
        to = destination,
        type = "template",
        template = new
        {
            name = configuration.NewOrderTemplateName,
            language = new { code = configuration.TemplateLanguage },
            components = new[]
            {
                new
                {
                    type = "body",
                    parameters = TemplateParameters(notification)
                        .Select(value => new { type = "text", text = value })
                        .ToArray()
                }
            }
        }
    };

    internal static string[] TemplateParameters(NewOrderWhatsAppNotification value) =>
    [
        value.OrderNumber,
        value.CustomerName,
        value.CustomerMobile,
        value.MealPlanName,
        value.Duration,
        value.MealsPerDay.ToString(CultureInfo.InvariantCulture),
        value.StartDate.ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
        value.DeliveryDays,
        value.DeliveryAddress,
        $"{value.Currency.Trim().ToUpperInvariant()} {value.TotalAmount.ToString("0.00", CultureInfo.InvariantCulture)}",
        ToDisplayStatus(value.OrderStatus)
    ];

    private static string ToDisplayStatus(string status)
    {
        var normalized = status.Trim().Replace('_', ' ').ToLowerInvariant();
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(normalized);
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        statusCode == HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static string? ReadMessageId(string responseBody)
    {
        try
        {
            using var json = JsonDocument.Parse(responseBody);
            return json.RootElement.GetProperty("messages")[0]
                .GetProperty("id").GetString();
        }
        catch (Exception exception) when (exception is JsonException or
            InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static WhatsAppSendResult ReadMetaFailure(
        HttpStatusCode statusCode,
        string responseBody)
    {
        try
        {
            using var json = JsonDocument.Parse(responseBody);
            var error = json.RootElement.GetProperty("error");
            var code = error.TryGetProperty("code", out var codeValue)
                ? codeValue.ToString()
                : ((int)statusCode).ToString(CultureInfo.InvariantCulture);
            var message = error.TryGetProperty("message", out var messageValue)
                ? messageValue.GetString()
                : null;
            return Failure(code, message ?? "Meta WhatsApp rejected the request.");
        }
        catch (Exception exception) when (exception is JsonException or
            InvalidOperationException or KeyNotFoundException)
        {
            return Failure(
                ((int)statusCode).ToString(CultureInfo.InvariantCulture),
                "Meta WhatsApp rejected the request.");
        }
    }

    private static WhatsAppSendResult Failure(string code, string message) => new()
    {
        Success = false,
        ErrorCode = code,
        ErrorMessage = message
    };
}
