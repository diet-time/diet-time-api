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
            Guid.NewGuid(), "ORD-20260814-000015", "Rugma", "+97433116397",
            "Balanced Living", "Monthly", 3, new DateOnly(2026, 8, 22),
            "Sat, Sun, Mon, Tue, Wed, Thu", "Pinned location, Qatar",
            3200m, "QAR", "CONFIRMED"));

        Assert.True(result.Success);
        var form = ParseForm(handler.Body!);
        Assert.Equal("whatsapp:+97474452435", form["To"]);
        Assert.Equal("HXneworder", form["ContentSid"]);
        using var variables = JsonDocument.Parse(form["ContentVariables"]);
        Assert.Equal(11, variables.RootElement.EnumerateObject().Count());
        Assert.Equal("ORD-20260814-000015", variables.RootElement.GetProperty("1").GetString());
        Assert.Equal("Rugma", variables.RootElement.GetProperty("2").GetString());
        Assert.Equal("+97433116397", variables.RootElement.GetProperty("3").GetString());
        Assert.Equal("Balanced Living", variables.RootElement.GetProperty("4").GetString());
        Assert.Equal("22 Aug 2026", variables.RootElement.GetProperty("5").GetString());
        Assert.Equal("Monthly", variables.RootElement.GetProperty("6").GetString());
        Assert.Equal("3 Meals", variables.RootElement.GetProperty("7").GetString());
        Assert.Equal("Sat, Sun, Mon, Tue, Wed, Thu", variables.RootElement.GetProperty("8").GetString());
        Assert.Equal("Pinned location, Qatar", variables.RootElement.GetProperty("9").GetString());
        Assert.Equal("QAR 3200.00", variables.RootElement.GetProperty("10").GetString());
        Assert.Equal("Confirmed", variables.RootElement.GetProperty("11").GetString());
    }

    [Fact]
    public async Task Invalid_new_order_data_is_rejected_before_calling_Twilio()
    {
        var handler = new RecordingHandler((_, _) => throw new InvalidOperationException());
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
            "Weight Loss", "", 3, new DateOnly(2026, 8, 15),
            "Sat, Sun, Mon", "Doha, Qatar", 499m, "QAR", "CONFIRMED"));

        Assert.False(result.Success);
        Assert.Equal("invalid_new_order_notification", result.ErrorCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public void Configuration_diagnostics_report_presence_and_format_without_returning_values()
    {
        var options = new TwilioWhatsAppOptions { AccountSid = "AC" + new string('1', 32), AuthToken = "private-token",
            FromNumber = "+14155238886", OperationsNumber = "+97455555555", NewOrderContentSid = "HX" + new string('2', 32) };
        var checks = options.ConfigurationChecks();
        Assert.Equal(5, checks.Count); Assert.All(checks.Values, Assert.True);
        options.NewOrderContentSid = "HXnot-a-real-sid";
        Assert.False(options.ConfigurationChecks()["NewOrderContentSid"]);
        Assert.DoesNotContain("private-token", JsonSerializer.Serialize(checks));
    }

    [Fact]
    public async Task Missing_message_sid_cannot_be_reported_as_a_success()
    {
        var handler = new RecordingHandler((_, _) => new(HttpStatusCode.Created) { Content = new StringContent("{\"status\":\"queued\"}") });
        var service = DiagnosticService(handler);
        var result = await service.SendTemplateAsync(new("+97455555555", "HXtemplate", new Dictionary<string,string> { ["1"] = "value" }));
        Assert.False(result.Success); Assert.Equal("invalid_provider_response", result.ErrorCode);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("undelivered")]
    public async Task Failed_provider_status_is_not_mistaken_for_delivery(string status)
    {
        var handler = new RecordingHandler((_, _) => new(HttpStatusCode.Created) {
            Content = new StringContent(JsonSerializer.Serialize(new { sid = "SMtest", status, error_code = 63015, error_message = "Sandbox recipient not joined" })) });
        var result = await DiagnosticService(handler).SendTemplateAsync(new("+97455555555", "HXtemplate", new Dictionary<string,string> { ["1"] = "value" }));
        Assert.False(result.Success); Assert.Equal("63015", result.ErrorCode); Assert.Equal(status, result.Status); Assert.Equal("SMtest", result.MessageId);
    }

    [Fact]
    public async Task Queued_provider_status_is_preserved_without_claiming_handset_delivery()
    {
        var handler = new RecordingHandler((_, _) => new(HttpStatusCode.Created) { Content = new StringContent("{\"sid\":\"SMtest\",\"status\":\"queued\"}") });
        var result = await DiagnosticService(handler).SendTemplateAsync(new("+97455555555", "HXtemplate", new Dictionary<string,string> { ["1"] = "value" }));
        Assert.True(result.Success); Assert.Equal("queued", result.Status);
    }

    [Fact]
    public async Task Disabled_new_order_is_logged_before_validating_optional_order_data()
    {
        var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("Must not send"));
        var logs = new DiagnosticLogger();
        var service = DiagnosticService(handler, enabled: false, logs);
        var result = await service.SendNewOrderNotificationAsync(new(Guid.NewGuid(), "ORD-1", "", "", "", "", 0, default, "", "", 0, "", ""));
        Assert.False(result.Success); Assert.Equal("twilio_whatsapp_disabled", result.ErrorCode); Assert.Equal(0, handler.CallCount);
        Assert.Contains(logs.Lines, line => line.Contains("twilio_whatsapp_disabled"));
    }

    [Fact]
    public async Task Rejections_log_provider_code_and_status_without_logging_customer_data()
    {
        var handler = new RecordingHandler((_, _) => new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"code\":21656,\"message\":\"Invalid variables\"}") });
        var logs = new DiagnosticLogger();
        var result = await DiagnosticService(handler, logger: logs).SendNewOrderNotificationAsync(new(Guid.NewGuid(), "ORD-1", "Private Customer", "+97455555555", "Balance", "Monthly", 4,
            new DateOnly(2026,10,29), "Sun, Mon", "Private address", 1000, "QAR", "CONFIRMED"));
        Assert.False(result.Success);
        Assert.Contains(logs.Lines, line => line.Contains("21656") && line.Contains("400"));
        var combined = string.Join(" ", logs.Lines);
        Assert.DoesNotContain("Private Customer", combined); Assert.DoesNotContain("Private address", combined); Assert.DoesNotContain("+97455555555", combined); Assert.DoesNotContain("private-token", combined);
    }


    [Theory]
    [InlineData("MM", "queued", false)]
    [InlineData("SM", "delivered", true)]
    [InlineData("MM", "read", true)]
    [InlineData("MM", "failed", false)]
    [InlineData("MM", "undelivered", false)]
    public async Task Lookup_reads_provider_status_without_sending_even_when_messaging_is_disabled(string prefix, string status, bool delivered)
    {
        var sid = prefix + new string('a', 32);
        var failed = status is "failed" or "undelivered";
        var handler = new RecordingHandler((_, _) => new(HttpStatusCode.OK) { Content = new StringContent(
            JsonSerializer.Serialize(new { sid, status, error_code = failed ? (int?)63015 : null, error_message = failed ? "Recipient not joined" : null, to = "private-recipient", body = "private-body" })) });
        var result = await DiagnosticService(handler, enabled: false).GetMessageStatusAsync(sid);
        Assert.True(result.Success); Assert.Equal(status, result.Status); Assert.Equal(delivered, result.DeliveryConfirmed);
        Assert.Equal(failed ? "63015" : null, result.ErrorCode);
        Assert.Equal("https://api.twilio.com/2010-04-01/Accounts/AC" + new string('1',32) + "/Messages/" + sid + ".json", handler.RequestUri);
        Assert.Equal("GET", handler.Method); Assert.Null(handler.Body); Assert.Equal(1, handler.CallCount);
        Assert.Equal("Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("AC" + new string('1',32) + ":private-token")), handler.Authorization);
        var json = JsonSerializer.Serialize(result); Assert.DoesNotContain("private-recipient", json); Assert.DoesNotContain("private-body", json);
    }

    [Theory]
    [InlineData("../Messages")]
    [InlineData("HXaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("MMtest")]
    public async Task Invalid_lookup_id_never_calls_provider(string sid)
    {
        var handler = new RecordingHandler((_, _) => throw new InvalidOperationException());
        var result = await DiagnosticService(handler).GetMessageStatusAsync(sid);
        Assert.False(result.Success); Assert.Equal("invalid_message_sid", result.ErrorCode); Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData(401, 20003)]
    [InlineData(404, 20404)]
    public async Task Lookup_preserves_provider_request_failure(int status, int code)
    {
        var handler = new RecordingHandler((_, _) => new((HttpStatusCode)status) { Content = new StringContent(JsonSerializer.Serialize(new { code, message = "Lookup rejected" })) });
        var result = await DiagnosticService(handler).GetMessageStatusAsync("MM" + new string('a',32));
        Assert.False(result.Success); Assert.Equal(status, result.ProviderHttpStatusCode); Assert.Equal(code.ToString(), result.ErrorCode);
    }

    [Fact]
    public async Task Lookup_rejects_mismatched_record_and_handles_timeout()
    {
        var sid = "MM" + new string('a',32);
        var handler = new RecordingHandler((_, _) => new(HttpStatusCode.OK) { Content = new StringContent("{\"sid\":\"wrong\",\"status\":\"delivered\"}") });
        Assert.Equal("invalid_provider_response", (await DiagnosticService(handler).GetMessageStatusAsync(sid)).ErrorCode);
        handler = new RecordingHandler((_, _) => throw new TaskCanceledException());
        Assert.Equal("timeout", (await DiagnosticService(handler).GetMessageStatusAsync(sid)).ErrorCode);
    }

    private static TwilioWhatsAppService DiagnosticService(RecordingHandler handler, bool enabled = true, DiagnosticLogger? logger = null) => new(
        new HttpClient(handler) { BaseAddress = new("https://api.twilio.com/") },
        Options.Create(new TwilioWhatsAppOptions { Enabled = enabled, AccountSid = "AC" + new string('1',32), AuthToken = "private-token",
            FromNumber = "+14155238886", OperationsNumber = "+97455555555", NewOrderContentSid = "HX" + new string('2',32) }),
        (Microsoft.Extensions.Logging.ILogger<TwilioWhatsAppService>?)logger ?? NullLogger<TwilioWhatsAppService>.Instance);

    private sealed class DiagnosticLogger : Microsoft.Extensions.Logging.ILogger<TwilioWhatsAppService>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
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
        public string? Method { get; private set; }
        public string? RequestUri { get; private set; }
        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method.Method;
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
