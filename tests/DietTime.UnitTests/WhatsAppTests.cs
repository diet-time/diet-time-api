using System.Net;
using System.Text.Json;
using DietTime.Application;
using DietTime.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DietTime.UnitTests;

public sealed class WhatsAppTests
{
    [Fact]
    public void Enabled_configuration_requires_new_order_destination_and_content_sid()
    {
        var options = new TwilioWhatsAppOptions
        {
            Enabled = true,
            AccountSid = "ACaccount",
            AuthToken = "secret-token",
            FromNumber = "+14155238886"
        };

        Assert.False(options.IsValid());
        options.OperationsNumber = "+97474452435";
        options.NewOrderContentSid = "HXneworder";
        Assert.True(options.IsValid());
    }

    [Theory]
    [InlineData("+97474452435", true)]
    [InlineData("whatsapp:+97474452435", true)]
    [InlineData("97474452435", false)]
    [InlineData("whatsapp:123", false)]
    public void Validates_Twilio_WhatsApp_phone_numbers(string value, bool expected) =>
        Assert.Equal(expected, TwilioWhatsAppPhoneNumber.IsValid(value));

    [Fact]
    public async Task Twilio_sends_content_template_as_form_with_basic_authentication()
    {
        var handler = new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("{\"sid\":\"SM123\",\"status\":\"queued\"}")
        });
        var service = new TwilioWhatsAppService(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.twilio.com/") },
            Options.Create(new TwilioWhatsAppOptions
            {
                Enabled = true,
                AccountSid = "ACaccount",
                AuthToken = "secret-token",
                FromNumber = "+14155238886"
            }),
            NullLogger<TwilioWhatsAppService>.Instance);

        var result = await service.SendTemplateAsync(new(
            "+97474452435",
            "HXb5b62575e6e4ff6129ad7c8efe1f983e",
            new Dictionary<string, string> { ["1"] = "12/1", ["2"] = "3pm" }));

        Assert.True(result.Success);
        Assert.Equal("SM123", result.MessageId);
        Assert.Equal("https://api.twilio.com/2010-04-01/Accounts/ACaccount/Messages.json", handler.RequestUri);
        Assert.Equal("Basic QUNhY2NvdW50OnNlY3JldC10b2tlbg==", handler.Authorization);
        var form = ParseForm(handler.Body!);
        Assert.Equal("whatsapp:+97474452435", form["To"]);
        Assert.Equal("whatsapp:+14155238886", form["From"]);
        Assert.Equal("HXb5b62575e6e4ff6129ad7c8efe1f983e", form["ContentSid"]);
        using var variables = JsonDocument.Parse(form["ContentVariables"]);
        Assert.Equal("12/1", variables.RootElement.GetProperty("1").GetString());
        Assert.Equal("3pm", variables.RootElement.GetProperty("2").GetString());
    }

    [Fact]
    public async Task Disabled_Twilio_configuration_does_not_send()
    {
        var handler = new RecordingHandler((_, _) => throw new InvalidOperationException());
        var service = new TwilioWhatsAppService(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.twilio.com/") },
            Options.Create(new TwilioWhatsAppOptions()),
            NullLogger<TwilioWhatsAppService>.Instance);

        var result = await service.SendTemplateAsync(new(
            "+97474452435", "HXtemplate", new Dictionary<string, string> { ["1"] = "value" }));

        Assert.False(result.Success);
        Assert.Equal("twilio_whatsapp_disabled", result.ErrorCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Twilio_rejection_returns_provider_code_without_retry()
    {
        var handler = new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"code\":21608,\"message\":\"Unverified recipient\"}")
        });
        var service = new TwilioWhatsAppService(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.twilio.com/") },
            Options.Create(new TwilioWhatsAppOptions
            {
                Enabled = true,
                AccountSid = "ACaccount",
                AuthToken = "secret-token",
                FromNumber = "+14155238886"
            }),
            NullLogger<TwilioWhatsAppService>.Instance);

        var result = await service.SendTemplateAsync(new(
            "+97474452435", "HXtemplate", new Dictionary<string, string> { ["1"] = "value" }));

        Assert.False(result.Success);
        Assert.Equal("21608", result.ErrorCode);
        Assert.Equal("Unverified recipient", result.ErrorMessage);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task New_order_uses_configured_operations_number_and_all_template_values()
    {
        var handler = new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("{\"sid\":\"SMorder\"}")
        });
        var service = new TwilioWhatsAppService(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.twilio.com/") },
            Options.Create(new TwilioWhatsAppOptions
            {
                Enabled = true,
                AccountSid = "ACaccount",
                AuthToken = "secret-token",
                FromNumber = "+14155238886",
                OperationsNumber = "+97474452435",
                NewOrderContentSid = "HXneworder"
            }),
            NullLogger<TwilioWhatsAppService>.Instance);

        var result = await service.SendNewOrderNotificationAsync(new(
            Guid.NewGuid(), "ORD-001", "Ahmed Ali", "+97450123456",
            "Weight Loss", "12 Days", 3, new DateOnly(2026, 8, 15),
            "Sat, Sun, Mon", "Doha, Qatar", 499m, "QAR", "CONFIRMED"));

        Assert.True(result.Success);
        var form = ParseForm(handler.Body!);
        Assert.Equal("whatsapp:+97474452435", form["To"]);
        Assert.Equal("HXneworder", form["ContentSid"]);
        using var variables = JsonDocument.Parse(form["ContentVariables"]);
        Assert.Equal(11, variables.RootElement.EnumerateObject().Count());
        Assert.Equal("ORD-001", variables.RootElement.GetProperty("1").GetString());
        Assert.Equal("+97450123456", variables.RootElement.GetProperty("3").GetString());
        Assert.Equal("QAR 499.00", variables.RootElement.GetProperty("10").GetString());
        Assert.Equal("Confirmed", variables.RootElement.GetProperty("11").GetString());
    }

    private static IReadOnlyDictionary<string, string> ParseForm(string body) =>
        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(field => field.Split('=', 2))
            .ToDictionary(
                field => Uri.UnescapeDataString(field[0].Replace('+', ' ')),
                field => Uri.UnescapeDataString(field[1].Replace('+', ' ')));

    private sealed class RecordingHandler(
        Func<int, CancellationToken, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public string? Body { get; private set; }
        public string? RequestUri { get; private set; }
        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            RequestUri = request.RequestUri?.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            return response(CallCount, cancellationToken);
        }
    }
}
