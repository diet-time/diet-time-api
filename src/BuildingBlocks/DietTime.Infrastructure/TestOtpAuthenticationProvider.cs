using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DietTime.Application;

namespace DietTime.Infrastructure;

// This provider is only used through the challenge service, never directly by AuthService.
public sealed class TestOtpAuthenticationProvider(OtpAuthenticationOptions options) : IPhoneOtpProvider
{
    public Task<PhoneOtpSendResult> SendAsync(string phoneNumber, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(options.IsTestAuthentication && options.AllowedTestNumbers.Contains(phoneNumber)
            ? new PhoneOtpSendResult(200, Guid.NewGuid().ToString("N")) : new PhoneOtpSendResult(400));
    }

    public Task<PhoneOtpVerificationStatus> VerifyAsync(string phoneNumber, string otp, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!options.IsTestAuthentication || !options.AllowedTestNumbers.Contains(phoneNumber) ||
            !Regex.IsMatch(otp, @"\A[0-9]{6}\z")) return Task.FromResult(PhoneOtpVerificationStatus.Invalid);
        return Task.FromResult(CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(otp), Encoding.UTF8.GetBytes(options.TestOtp))
            ? PhoneOtpVerificationStatus.Valid : PhoneOtpVerificationStatus.Invalid);
    }

    public Task<bool> CancelAsync(string requestId, CancellationToken ct) => Task.FromResult(true);
}
