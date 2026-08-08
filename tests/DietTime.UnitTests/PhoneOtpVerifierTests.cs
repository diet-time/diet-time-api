using DietTime.Application;
using DietTime.Infrastructure;
using Microsoft.Extensions.Options;

namespace DietTime.UnitTests;

public sealed class PhoneOtpVerifierTests
{
    [Fact]
    public async Task Configured_test_code_is_accepted()
    {
        var verifier = Create(enabled: true, code: "123456");

        var result = await verifier.VerifyAsync("+97455555555", "123456", default);

        Assert.Equal(PhoneOtpVerificationStatus.Valid, result);
    }

    [Fact]
    public async Task Incorrect_test_code_is_rejected()
    {
        var verifier = Create(enabled: true, code: "123456");

        var result = await verifier.VerifyAsync("+97455555555", "654321", default);

        Assert.Equal(PhoneOtpVerificationStatus.Invalid, result);
    }

    [Fact]
    public async Task Test_login_is_disabled_unless_explicitly_enabled()
    {
        var verifier = Create(enabled: false, code: "123456");

        var result = await verifier.VerifyAsync("+97455555555", "123456", default);

        Assert.Equal(PhoneOtpVerificationStatus.Disabled, result);
    }

    private static TestPhoneOtpVerifier Create(bool enabled, string code) =>
        new(Options.Create(new PhoneOtpOptions { Enabled = enabled, TestCode = code }));
}
