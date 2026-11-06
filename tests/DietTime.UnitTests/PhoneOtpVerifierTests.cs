using DietTime.Application;
using DietTime.Infrastructure;

namespace DietTime.UnitTests;

public sealed class PhoneOtpVerifierTests
{
    private static TestOtpAuthenticationProvider Create(bool enabled = true) => new(new() {
        IsTestAuthentication = enabled, TestOtp = "123456", AllowedTestNumbers = new HashSet<string> { "+97455555555" }
    });

    [Fact]
    public async Task Configured_test_code_only_authenticates_allowlisted_numbers()
    {
        Assert.Equal(PhoneOtpVerificationStatus.Valid, await Create().VerifyAsync("+97455555555", "123456", default));
        Assert.Equal(PhoneOtpVerificationStatus.Invalid, await Create().VerifyAsync("+97455555556", "123456", default));
        Assert.Equal(400, (await Create().SendAsync("+97455555556", default)).StatusCode);
    }
    [Fact]
    public async Task Wrong_code_and_disabled_test_provider_never_authenticate()
    {
        Assert.Equal(PhoneOtpVerificationStatus.Invalid, await Create().VerifyAsync("+97455555555", "654321", default));
        Assert.Equal(PhoneOtpVerificationStatus.Invalid, await Create(false).VerifyAsync("+97455555555", "123456", default));
    }
    [Fact]
    public async Task Test_send_returns_a_challenge_identifier_and_never_the_code()
    {
        var result = await Create().SendAsync("+97455555555", default);
        Assert.Equal(200, result.StatusCode);
        Assert.NotEqual("123456", result.RequestId);
        Assert.DoesNotContain("123456", System.Text.Json.JsonSerializer.Serialize(result));
    }
}
