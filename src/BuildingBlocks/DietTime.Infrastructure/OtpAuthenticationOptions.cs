using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace DietTime.Infrastructure;

public sealed class OtpAuthenticationOptions
{
    public bool IsTestAuthentication { get; init; }
    public string TestOtp { get; init; } = "";
    public IReadOnlySet<string> AllowedTestNumbers { get; init; } = new HashSet<string>();
    public const int LifetimeSeconds = 300;
    public const int ResendSeconds = 60;
    public const int MaximumAttempts = 5;

    public static OtpAuthenticationOptions Load(IConfiguration configuration, string environment,
        TwilioVerifyOptions verify, TwilioWhatsAppOptions credentials)
    {
        // Missing/malformed mode is live, and still requires valid live configuration.
        var test = bool.TryParse(configuration["IsTestAuthentication"], out var enabled) && enabled;
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        var configuredNumbers = (configuration["TestAuthenticationAllowedPhoneNumbers"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (test)
        {
            if (string.Equals(environment, "Production", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("IsTestAuthentication cannot be enabled in Production. Use a separate non-public test deployment.");
            if (!Regex.IsMatch(configuration["TestAuthenticationOtp"] ?? "", @"\A[0-9]{6}\z"))
                throw new InvalidOperationException("TestAuthenticationOtp must contain exactly six digits.");
            foreach (var number in configuredNumbers)
            {
                if (!PhoneOtpNumbers.TryNormalize(number, out var normalized))
                    throw new InvalidOperationException("TestAuthenticationAllowedPhoneNumbers must contain E.164 numbers separated by commas.");
                allowed.Add(normalized);
            }
            if (allowed.Count == 0)
                throw new InvalidOperationException("Test authentication requires a non-empty TestAuthenticationAllowedPhoneNumbers allowlist.");
        }
        else
        {
            // Report setting names and expected formats only; never include credential values.
            var invalid = new List<string>();
            if (!Regex.IsMatch(verify.ServiceSid ?? "", @"\AVA[0-9a-fA-F]{32}\z"))
                invalid.Add("TwilioVerify__ServiceSid (alias TWILIO_VERIFY_SERVICE_SID; VA followed by 32 hexadecimal characters)");
            if (!Regex.IsMatch(credentials.AccountSid ?? "", @"\AAC[0-9a-fA-F]{32}\z"))
                invalid.Add("TwilioWhatsApp__AccountSid (alias TWILIO_ACCOUNT_SID; AC followed by 32 hexadecimal characters)");
            if (string.IsNullOrWhiteSpace(credentials.AuthToken))
                invalid.Add("TwilioWhatsApp__AuthToken (alias TWILIO_AUTH_TOKEN; non-empty)");
            if (invalid.Count > 0)
                throw new InvalidOperationException("Live authentication configuration is missing or invalid: " +
                    string.Join("; ", invalid) + ". Set these variables on the API service and redeploy. " +
                    "Non-empty nested Twilio settings take precedence over aliases.");
        }
        return new() {
            IsTestAuthentication = test,
            // Live mode never retains the configured test OTP or allowlist.
            TestOtp = test ? configuration["TestAuthenticationOtp"]! : "",
            AllowedTestNumbers = allowed
        };
    }
}

public static class PhoneOtpNumbers
{
    public static bool TryNormalize(string? input, out string number)
    {
        number = Regex.Replace(input?.Trim() ?? "", @"[\s().-]", "");
        if (number.StartsWith("00", StringComparison.Ordinal)) number = "+" + number[2..];
        return Regex.IsMatch(number, @"\A\+[1-9][0-9]{7,14}\z");
    }
}
