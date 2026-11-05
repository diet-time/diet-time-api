using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DietTime.Application;
using Microsoft.Extensions.Options;

namespace DietTime.Infrastructure;

public sealed class TwilioVerifyOptions
{
    public const string SectionName = "TwilioVerify";
    public bool Enabled { get; set; }
    public string ServiceSid { get; set; } = "";
}

// Verify owns code generation, expiry, attempt limits and consumption.
// No static test-code fallback is used by this service.
public sealed class TwilioPhoneOtpService(
    HttpClient client,
    IOptions<TwilioVerifyOptions> options,
    IOptions<TwilioWhatsAppOptions> credentials) : IPhoneOtpVerifier
{
    public async Task<PhoneOtpSendResult> SendAsync(string phoneNumber, CancellationToken ct)
    {
        if (!Configured()) return new(503);
        var result = await PostAsync("Verifications", new() { ["To"] = phoneNumber, ["Channel"] = "whatsapp" }, ct);
        if (result.StatusCode != 200) return new(result.StatusCode);
        if (result.Status != "pending" || result.To != phoneNumber || result.Channel != "whatsapp" ||
            !Regex.IsMatch(result.Sid ?? "", @"\AVE[0-9a-fA-F]{32}\z")) return new(503);
        return new(200, result.Sid);
    }

    public async Task<PhoneOtpVerificationStatus> VerifyAsync(string phoneNumber, string otp, CancellationToken cancellationToken)
    {
        if (!Configured()) return PhoneOtpVerificationStatus.Disabled;
        if (!Regex.IsMatch(otp, @"\A[0-9]{6}\z")) return PhoneOtpVerificationStatus.Invalid;
        var result = await PostAsync("VerificationCheck", new() { ["To"] = phoneNumber, ["Code"] = otp }, cancellationToken);
        if (result.StatusCode == 429) return PhoneOtpVerificationStatus.TooManyAttempts;
        if (result.StatusCode == 503) return PhoneOtpVerificationStatus.Disabled;
        return result.StatusCode == 200 && result.Status == "approved" && result.To == phoneNumber && result.Channel == "whatsapp"
            ? PhoneOtpVerificationStatus.Valid : PhoneOtpVerificationStatus.Invalid;
    }

    private bool Configured() => options.Value.Enabled &&
        Regex.IsMatch(options.Value.ServiceSid, @"\AVA[0-9a-fA-F]{32}\z") &&
        Regex.IsMatch(credentials.Value.AccountSid, @"\AAC[0-9a-fA-F]{32}\z") &&
        !string.IsNullOrWhiteSpace(credentials.Value.AuthToken);

    private async Task<ProviderResult> PostAsync(string resource, Dictionary<string, string> fields, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://verify.twilio.com/v2/Services/{options.Value.ServiceSid}/{resource}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{credentials.Value.AccountSid}:{credentials.Value.AuthToken}")));
            request.Content = new FormUrlEncodedContent(fields);
            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) return new(429);
            // An expired, consumed or missing verification is reported as 404.
            if (response.StatusCode == HttpStatusCode.NotFound && resource == "VerificationCheck") return new(401);
            if (!response.IsSuccessStatusCode) return new(503);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            string? Read(string name) => json.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new(200, Read("status"), Read("sid"), Read("to"), Read("channel"));
        }
        catch (HttpRequestException) { return new(503); }
        catch (JsonException) { return new(503); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(503); }
    }

    private sealed record ProviderResult(int StatusCode, string? Status = null, string? Sid = null, string? To = null, string? Channel = null);
}

public sealed record PhoneOtpSendResult(int StatusCode, string? RequestId = null);
