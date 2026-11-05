using DietTime.Infrastructure;
using Microsoft.Extensions.Options;
using DietTime.Application;

namespace DietTime.UnitTests;

public sealed class PhoneOtpModeTests
{
    [Fact]
    public void Staging_uses_live_verification_by_default() =>
        Assert.False(new PhoneOtpOptions().ShouldUseTestVerifier("Staging", false));

    [Fact]
    public void Staging_can_explicitly_use_test_otp_even_with_Twilio_enabled() =>
        Assert.True(TestOptions().ShouldUseTestVerifier("Staging", true));

    [Fact]
    public void Production_rejects_test_mode() =>
        Assert.Throws<InvalidOperationException>(() => TestOptions().ShouldUseTestVerifier("Production", false));

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("abcdef")]
    [InlineData("123456\n")]
    public void Explicit_test_mode_requires_six_digits(string code) =>
        Assert.Throws<InvalidOperationException>(() => new PhoneOtpOptions {
            UseTestOtp = true, Enabled = true, TestCode = code
        }.ShouldUseTestVerifier("Staging", false));

    [Fact]
    public async Task Test_verifier_still_rejects_wrong_codes()
    {
        var verifier = new TestPhoneOtpVerifier(Options.Create(TestOptions()));
        Assert.Equal(PhoneOtpVerificationStatus.Valid, await verifier.VerifyAsync("+97455555555", "123456", default));
        Assert.Equal(PhoneOtpVerificationStatus.Invalid, await verifier.VerifyAsync("+97455555555", "654321", default));
    }

    private static PhoneOtpOptions TestOptions() => new() { UseTestOtp = true, Enabled = true, TestCode = "123456" };
}
