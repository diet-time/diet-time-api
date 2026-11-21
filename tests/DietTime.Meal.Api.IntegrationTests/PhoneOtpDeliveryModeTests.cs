using DietTime.Application;
using DietTime.Meal.Api.Controllers;
using DietTime.Meal.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed class PhoneOtpDeliveryModeTests
{
    [Theory]
    [InlineData("97455555555", "whatsapp", "InvalidInternationalPhone")]
    [InlineData("+97455555555", "SMS-private-submitted-value", "InvalidWhatsAppChannel")]
    public async Task Rejection_diagnostic_is_available_to_request_logging_without_submitted_values(
        string phone, string channel, string reason)
    {
        var service = new Stub(new(200, "opaque-request"));
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/v1/auth/phone-otp/request";
        var controller = new PhoneOtpDeliveryController(service) {
            ControllerContext = new() { HttpContext = context }
        };

        Assert.IsType<BadRequestObjectResult>(await controller.RequestCode(new(phone, channel), default));
        var diagnostic = OtpRequestDiagnostics.Summary(context);
        Assert.Contains("OtpDiagnostics=v2", diagnostic);
        Assert.Contains("Reason=" + reason, diagnostic);
        Assert.DoesNotContain(phone, diagnostic);
        Assert.DoesNotContain(channel, diagnostic);
        Assert.Null(service.Phone);
    }

    [Fact]
    public void Request_logging_reports_rejection_even_when_controller_does_not_execute()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/v1/auth/phone-otp/request";
        context.Response.StatusCode = 400;

        Assert.Contains("Reason=RejectedBeforeOtpAction", OtpRequestDiagnostics.Summary(context));
    }

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
