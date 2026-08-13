using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DietTime.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DietTime.Infrastructure;

public sealed class TwilioWhatsAppOptions
{
    public const string SectionName = "TwilioWhatsApp";

    public bool Enabled { get; set; }
    public string AccountSid { get; set; } = string.Empty;
    public string AuthToken { get; set; } = string.Empty;
    public string FromNumber { get; set; } = string.Empty;

    public bool IsValid() => !Enabled ||
        (AccountSid.StartsWith("AC", StringComparison.Ordinal) && AccountSid.Length > 2 &&
         !string.IsNullOrWhiteSpace(AuthToken) &&
         TwilioWhatsAppPhoneNumber.IsValid(FromNumber));
}

public static class TwilioWhatsAppPhoneNumber
{
    public static bool IsValid(string? value)
    {
        var number = RemovePrefix(value);
        return number.Length is >= 8 and <= 16 && number[0] == '+' &&
            number.Skip(1).All(char.IsDigit) && number[1] != '0';
    }

    public static string Format(string value) => $"whatsapp:{RemovePrefix(value.Trim())}";

    private static string RemovePrefix(string? value) =>
        (value ?? string.Empty).Trim().StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase)
            ? (value ?? string.Empty).Trim()["whatsapp:".Length..].Trim()
            : (value ?? string.Empty).Trim();
}

public sealed class TwilioWhatsAppService(
    HttpClient httpClient,
    IOptions<TwilioWhatsAppOptions> options,
    ILogger<TwilioWhatsAppService> logger) : ITwilioWhatsAppService
{
    private const int MaximumAttempts = 3;

    public async Task<WhatsAppSendResult> SendTemplateAsync(
        TwilioWhatsAppTemplateMessage message,
        CancellationToken cancellationToken = default)
    {
        var configuration = options.Value;
        if (!configuration.Enabled)
            return Failure("twilio_whatsapp_disabled", "Twilio WhatsApp messaging is disabled.");

        var variables = message.ContentVariables.ToDictionary(
            pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var fields = new Dictionary<string, string>
        {
            ["To"] = TwilioWhatsAppPhoneNumber.Format(message.To),
            ["From"] = TwilioWhatsAppPhoneNumber.Format(configuration.FromNumber),
            ["ContentSid"] = message.ContentSid.Trim(),
            ["ContentVariables"] = JsonSerializer.Serialize(variables)
        };
        var endpoint = $"2010-04-01/Accounts/{configuration.AccountSid.Trim()}/Messages.json";

        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new FormUrlEncodedContent(fields)
                };
                var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    $"{configuration.AccountSid.Trim()}:{configuration.AuthToken}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

                using var response = await httpClient.SendAsync(request, cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var sid = ReadString(responseBody, "sid");
                    logger.LogInformation("Twilio WhatsApp template accepted. MessageSid={MessageSid}", sid);
                    return new WhatsAppSendResult { Success = true, MessageId = sid };
                }

                var failure = ReadFailure(response.StatusCode, responseBody);
                if (!IsTransient(response.StatusCode) || attempt == MaximumAttempts)
                    return failure;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt == MaximumAttempts)
                    return Failure("timeout", "The Twilio WhatsApp request timed out.");
            }
            catch (HttpRequestException)
            {
                if (attempt == MaximumAttempts)
                    return Failure("http_error", "Twilio WhatsApp could not be reached.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken);
        }

        return Failure("unknown_error", "Twilio WhatsApp did not return a result.");
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static WhatsAppSendResult ReadFailure(HttpStatusCode statusCode, string body)
    {
        var code = ReadString(body, "code") ?? ((int)statusCode).ToString(CultureInfo.InvariantCulture);
        var message = ReadString(body, "message") ?? "Twilio rejected the WhatsApp message.";
        return Failure(code, message);
    }

    private static string? ReadString(string body, string property)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty(property, out var value)) return null;
            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static WhatsAppSendResult Failure(string code, string message) => new()
    {
        Success = false,
        ErrorCode = code,
        ErrorMessage = message
    };
}
