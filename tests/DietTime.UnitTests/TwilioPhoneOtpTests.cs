using System.Net;
using System.Text;
using DietTime.Application;
using DietTime.Infrastructure;
using Microsoft.Extensions.Options;

namespace DietTime.UnitTests;

public sealed class TwilioPhoneOtpTests
{
    [Fact]
    public async Task Sends_WhatsApp_and_never_a_test_code()
    {
        var handler = new Handler(HttpStatusCode.Created, "pending");
        var result = await Create(handler).SendAsync("+97455555555", default);
        Assert.Equal(200, result.StatusCode);
        Assert.EndsWith("/Verifications", handler.Path);
        Assert.Contains("Channel=whatsapp", handler.Body);
        Assert.Contains("To=%2B97455555555", handler.Body);
        Assert.DoesNotContain("Code=", handler.Body);
        Assert.Equal("Basic", handler.Authentication);
    }

    [Theory]
    [InlineData("approved", PhoneOtpVerificationStatus.Valid)]
    [InlineData("pending", PhoneOtpVerificationStatus.Invalid)]
    [InlineData("expired", PhoneOtpVerificationStatus.Invalid)]
    public async Task Only_approved_codes_authenticate(string status, PhoneOtpVerificationStatus expected)
    {
        var handler = new Handler(HttpStatusCode.OK, status);
        Assert.Equal(expected, await Create(handler).VerifyAsync("+97455555555", "123456", default));
        Assert.EndsWith("/VerificationCheck", handler.Path);
        Assert.Contains("Code=123456", handler.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, PhoneOtpVerificationStatus.Invalid)]
    [InlineData(HttpStatusCode.TooManyRequests, PhoneOtpVerificationStatus.TooManyAttempts)]
    [InlineData(HttpStatusCode.Unauthorized, PhoneOtpVerificationStatus.Disabled)]
    [InlineData(HttpStatusCode.ServiceUnavailable, PhoneOtpVerificationStatus.Disabled)]
    public async Task Provider_failures_cannot_authenticate(HttpStatusCode httpStatus, PhoneOtpVerificationStatus expected)
    {
        Assert.Equal(expected, await Create(new Handler(httpStatus, "approved"))
            .VerifyAsync("+97455555555", "123456", default));
    }

    [Fact]
    public async Task Missing_configuration_does_not_send()
    {
        var handler = new Handler(HttpStatusCode.OK, "approved");
        var service = new TwilioPhoneOtpService(new HttpClient(handler), Options.Create(new TwilioVerifyOptions()),
            Options.Create(new TwilioWhatsAppOptions()));
        Assert.Equal(503, (await service.SendAsync("+97455555555", default)).StatusCode);
        Assert.Equal(PhoneOtpVerificationStatus.Disabled, await service.VerifyAsync("+97455555555", "123456", default));
        Assert.Null(handler.Path);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    [InlineData("123456\n")]
    public async Task Malformed_codes_never_reach_provider(string code)
    {
        var handler = new Handler(HttpStatusCode.OK, "approved");
        Assert.Equal(PhoneOtpVerificationStatus.Invalid, await Create(handler).VerifyAsync("+97455555555", code, default));
        Assert.Null(handler.Path);
    }

    private static TwilioPhoneOtpService Create(Handler handler) => new(new HttpClient(handler),
        Options.Create(new TwilioVerifyOptions { Enabled = true, ServiceSid = "VA" + new string('1', 32) }),
        Options.Create(new TwilioWhatsAppOptions { AccountSid = "AC" + new string('2', 32), AuthToken = "test-credential" }));

    private sealed class Handler(HttpStatusCode statusCode, string status) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public string? Body { get; private set; }
        public string? Authentication { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Path = request.RequestUri!.AbsolutePath;
            Authentication = request.Headers.Authorization?.Scheme;
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new(statusCode) { Content = new StringContent(
                "{\"status\":\"" + status + "\",\"sid\":\"VE" + new string('3', 32) +
                "\",\"to\":\"+97455555555\",\"channel\":\"whatsapp\"}", Encoding.UTF8, "application/json") };
        }
    }
}
