using DietTime.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace DietTime.UnitTests;

public sealed class PhoneOtpModeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("not-a-boolean")]
    public void Missing_or_invalid_mode_never_enables_test_authentication(string? mode)
    {
        var config = Config(mode);
        var options = OtpAuthenticationOptions.Load(config, "Staging", Live(), Credentials());
        Assert.False(options.IsTestAuthentication);
        Assert.Empty(options.TestOtp);
        Assert.Empty(options.AllowedTestNumbers);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("broken")]
    public void Invalid_live_configuration_fails_startup(string? mode) =>
        Assert.Throws<InvalidOperationException>(() => OtpAuthenticationOptions.Load(Config(mode), "Staging", new(), new()));

    [Fact]
    public void Production_refuses_fixed_code_authentication() =>
        Assert.Throws<InvalidOperationException>(() => OtpAuthenticationOptions.Load(Config("true"), "Production", new(), new()));

    [Fact]
    public void Test_mode_requires_allowlist()
    {
        var config = Config("true"); config["TestAuthenticationAllowedPhoneNumbers"] = "";
        Assert.Throws<InvalidOperationException>(() => OtpAuthenticationOptions.Load(config, "Staging", new(), new()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("abcdef")]
    [InlineData("123456\n")]
    public void Invalid_test_codes_fail_startup(string code)
    {
        var config = Config("true"); config["TestAuthenticationOtp"] = code;
        Assert.Throws<InvalidOperationException>(() => OtpAuthenticationOptions.Load(config, "Development", new(), new()));
    }

    [Fact]
    public void Allowlist_and_input_numbers_share_the_same_canonical_form()
    {
        var options = OtpAuthenticationOptions.Load(Config("true"), "Staging", new(), new());
        Assert.True(options.IsTestAuthentication);
        Assert.Contains("+97455555555", options.AllowedTestNumbers);
        Assert.True(PhoneOtpNumbers.TryNormalize("00974 (5555) 5555", out var phone));
        Assert.Equal("+97455555555", phone);
        Assert.False(PhoneOtpNumbers.TryNormalize("+97455555555oops", out _));
    }

    private static IConfiguration Config(string? mode) => new Settings(new Dictionary<string, string?> {
        ["IsTestAuthentication"] = mode,
        ["TestAuthenticationOtp"] = "123456",
        ["TestAuthenticationAllowedPhoneNumbers"] = "+974 5555 5555"
    });
    private static TwilioVerifyOptions Live() => new() { ServiceSid = "VA" + new string('1',32) };
    private static TwilioWhatsAppOptions Credentials() => new() { AccountSid = "AC" + new string('2',32), AuthToken = "test-only" };
    private sealed class Settings(Dictionary<string, string?> values) : IConfiguration
    {
        public string? this[string key] { get => values.GetValueOrDefault(key); set => values[key] = value; }
        public IEnumerable<IConfigurationSection> GetChildren() => [];
        public Microsoft.Extensions.Primitives.IChangeToken GetReloadToken() => throw new NotSupportedException();
        public IConfigurationSection GetSection(string key) => throw new NotSupportedException();
    }
}
