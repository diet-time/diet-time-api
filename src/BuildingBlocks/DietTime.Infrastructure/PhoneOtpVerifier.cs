using System.Security.Cryptography;
using System.Text;
using DietTime.Application;
using Microsoft.Extensions.Options;

namespace DietTime.Infrastructure;

public sealed class PhoneOtpOptions
{
    public const string SectionName = "PhoneOtp";
    public bool Enabled { get; set; }
    public string TestCode { get; set; } = "";
    public bool UseTestOtp { get; set; }

    public bool ShouldUseTestVerifier(string environmentName, bool liveVerificationEnabled)
    {
        if (UseTestOtp && string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("PhoneOtp:UseTestOtp cannot be enabled in Production.");
        if (UseTestOtp && (!Enabled || !System.Text.RegularExpressions.Regex.IsMatch(TestCode ?? "", @"\A[0-9]{6}\z")))
            throw new InvalidOperationException("Test OTP requires PhoneOtp:Enabled=true and a six-digit PhoneOtp:TestCode.");
        return UseTestOtp || (string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase) && !liveVerificationEnabled);
    }
}

/// <summary>
/// Temporary OTP verifier. Replace this registration with a Twilio-backed
/// IPhoneOtpVerifier when live phone verification is enabled.
/// </summary>
public sealed class TestPhoneOtpVerifier(IOptions<PhoneOtpOptions> options) : IPhoneOtpVerifier
{
    private readonly PhoneOtpOptions settings = options.Value;

    public Task<PhoneOtpVerificationStatus> VerifyAsync(
        string phoneNumber,
        string otp,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!settings.Enabled || !System.Text.RegularExpressions.Regex.IsMatch(settings.TestCode ?? "", @"\A[0-9]{6}\z"))
            return Task.FromResult(PhoneOtpVerificationStatus.Disabled);

        var supplied = Encoding.UTF8.GetBytes(otp);
        var expected = Encoding.UTF8.GetBytes(settings.TestCode ?? "");
        var valid = supplied.Length == expected.Length &&
                    CryptographicOperations.FixedTimeEquals(supplied, expected);
        return Task.FromResult(valid
            ? PhoneOtpVerificationStatus.Valid
            : PhoneOtpVerificationStatus.Invalid);
    }
}
