using DietTime.Infrastructure;
using DietTime.Meal.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed class PhoneOtpDeliveryModeTests
{
    [Fact]
    public async Task Server_test_mode_never_calls_WhatsApp_even_for_an_old_client()
    {
        var handler = new NeverSend();
        var sender = new TwilioPhoneOtpService(new HttpClient(handler),
            Options.Create(new TwilioVerifyOptions { Enabled = true, ServiceSid = "VA" + new string('1', 32) }),
            Options.Create(new TwilioWhatsAppOptions { AccountSid = "AC" + new string('2', 32), AuthToken = "test-only" }));
        var controller = new PhoneOtpDeliveryController(sender, Options.Create(new PhoneOtpOptions { UseTestOtp = true }));
        Assert.IsType<OkObjectResult>(await controller.RequestCode(new("+97455555555"), default));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Unconfigured_live_delivery_is_unavailable_and_does_not_pretend_to_send()
    {
        var handler = new NeverSend();
        var sender = new TwilioPhoneOtpService(new HttpClient(handler),
            Options.Create(new TwilioVerifyOptions()), Options.Create(new TwilioWhatsAppOptions()));
        var controller = new PhoneOtpDeliveryController(sender, Options.Create(new PhoneOtpOptions()));
        var response = Assert.IsType<ObjectResult>(await controller.RequestCode(new("+97455555555"), default));
        Assert.Equal(503, response.StatusCode);
        Assert.Equal(0, handler.Calls);
    }

    private sealed class NeverSend : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("Test must never contact WhatsApp.");
        }
    }
}
