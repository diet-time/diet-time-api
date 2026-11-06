using DietTime.Application;
using DietTime.Meal.Api.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed class PhoneOtpDeliveryModeTests
{
    [Fact]
    public async Task Request_contract_normalizes_number_and_returns_no_OTP()
    {
        var service = new Stub(new(200, "opaque-request"));
        var controller = new PhoneOtpDeliveryController(service);
        var response = Assert.IsType<OkObjectResult>(await controller.RequestCode(new("00974 (5555) 5555"), default));
        Assert.Equal("+97455555555", service.Phone);
        var json = System.Text.Json.JsonSerializer.Serialize(response.Value);
        Assert.DoesNotContain("123456", json);
        Assert.Contains("ResendAfterSeconds", json);
    }
    [Fact]
    public async Task Cooldown_returns_retry_after_without_a_success_response()
    {
        var controller = new PhoneOtpDeliveryController(new Stub(new(429, RetryAfterSeconds: 47))) {
            ControllerContext = new() { HttpContext = new DefaultHttpContext() }
        };
        Assert.Equal(429, Assert.IsType<ObjectResult>(await controller.RequestCode(new("+97455555555"), default)).StatusCode);
        Assert.Equal("47", controller.Response.Headers["Retry-After"]);
    }
    private sealed class Stub(PhoneOtpSendResult response) : IPhoneOtpAuthenticationService
    {
        public string? Phone;
        public Task<PhoneOtpSendResult> SendAsync(string phoneNumber, CancellationToken ct) { Phone = phoneNumber; return Task.FromResult(response); }
        public Task<PhoneOtpVerificationStatus> VerifyAsync(string phoneNumber, string otp, CancellationToken ct) => throw new NotImplementedException();
    }
}
