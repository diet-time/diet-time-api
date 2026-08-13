using System.Net;
using System.Text;
using System.Text.Json;
using DietTime.Application;
using DietTime.Infrastructure;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DietTime.UnitTests;

public sealed class WhatsAppTests
{
    [Fact]
    public void Development_configuration_contains_the_operations_number()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "appsettings.Development.json")));

        Assert.Equal(
            "+97474452435",
            json.RootElement.GetProperty("WhatsApp")
                .GetProperty("OperationsNumber").GetString());
    }

    [Theory]
    [InlineData("+974 7445 2435", "97474452435")]
    [InlineData("(+974)-7445-2435", "97474452435")]
    public void Operations_number_is_normalized_for_Meta(string input, string expected) =>
        Assert.Equal(expected, WhatsAppPhoneNumber.Normalize(input));

    [Fact]
    public async Task Sends_the_configured_template_with_all_parameters_in_order()
    {
        var handler = new RecordingHandler((_, _) => MetaSuccess("wamid.123"));
        var service = Service(handler);

        var result = await service.SendNewOrderNotificationAsync(Notification());

        Assert.True(result.Success);
        Assert.Equal("wamid.123", result.MessageId);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal("Bearer meta-token", handler.Authorization);
        Assert.Equal("https://graph.facebook.com/v22.0/phone-id/messages", handler.RequestUri);

        using var json = JsonDocument.Parse(handler.Body!);
        var root = json.RootElement;
        Assert.Equal("97474452435", root.GetProperty("to").GetString());
        var template = root.GetProperty("template");
        Assert.Equal("new_order_summary", template.GetProperty("name").GetString());
        Assert.Equal("en", template.GetProperty("language").GetProperty("code").GetString());
        var values = template.GetProperty("components")[0].GetProperty("parameters")
            .EnumerateArray().Select(x => x.GetProperty("text").GetString()).ToArray();
        Assert.Equal(new string?[]
        {
            "ORD-20260813-001", "Ahmed Ali", "+97450123456", "Weight Loss Plan",
            "12 Days", "3", "15 Aug 2026", "Sat, Sun, Mon, Tue, Wed, Thu",
            "Apartment 1204, Doha, Qatar", "QAR 499.00", "Confirmed"
        }, values);
    }

    [Fact]
    public async Task Permanent_Meta_rejection_is_returned_without_retry()
    {
        var handler = new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"error\":{\"code\":131030,\"message\":\"Recipient is not registered\"}}",
                Encoding.UTF8,
                "application/json")
        });

        var result = await Service(handler).SendNewOrderNotificationAsync(Notification());

        Assert.False(result.Success);
        Assert.Equal("131030", result.ErrorCode);
        Assert.Equal("Recipient is not registered", result.ErrorMessage);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Timeout_has_bounded_retries_and_returns_failure()
    {
        var handler = new RecordingHandler((_, _) => throw new TaskCanceledException());

        var result = await Service(handler).SendNewOrderNotificationAsync(Notification());

        Assert.False(result.Success);
        Assert.Equal("timeout", result.ErrorCode);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task Disabled_configuration_does_not_call_Meta()
    {
        var handler = new RecordingHandler((_, _) => MetaSuccess("unexpected"));
        var options = Options.Create(new WhatsAppOptions { Enabled = false });
        var service = new MetaWhatsAppService(
            Client(handler), options, NullLogger<MetaWhatsAppService>.Instance);

        var result = await service.SendNewOrderNotificationAsync(Notification());

        Assert.False(result.Success);
        Assert.Equal("whatsapp_disabled", result.ErrorCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public void Enabled_configuration_requires_provider_credentials()
    {
        Assert.False(new WhatsAppOptions
        {
            Enabled = true,
            OperationsNumber = "+97474452435"
        }.IsValid());
        Assert.True(OptionsValue().IsValid());
    }

    [Fact]
    public void Order_summary_uses_service_day_order_and_clean_address_snapshot()
    {
        var order = new Order
        {
            StartDate = new DateOnly(2026, 8, 15),
            EndDate = new DateOnly(2026, 8, 23),
            DeliveryAddressName = "Home",
            DeliveryBuildingNo = "25",
            DeliveryStreetNo = "320",
            DeliveryArea = "Al Waab"
        };
        foreach (var day in new[] { 1, 2, 3, 4, 6, 7 })
            order.DeliveryDays.Add(new OrderDeliveryDay { DayOfWeek = day });

        Assert.Equal(
            "Sat, Sun, Mon, Tue, Wed, Thu",
            OrderService.FormatDeliveryDays(order));
        Assert.Equal(
            "Home, Building 25, Street 320, Al Waab",
            OrderService.FormatDeliveryAddress(order));

        order.DeliveryFormattedAddress = "Villa 25, Street 320, Al Waab, Doha, Qatar";
        Assert.Equal(
            "Villa 25, Street 320, Al Waab, Doha, Qatar",
            OrderService.FormatDeliveryAddress(order));
    }

    private static MetaWhatsAppService Service(HttpMessageHandler handler) => new(
        Client(handler),
        Options.Create(OptionsValue()),
        NullLogger<MetaWhatsAppService>.Instance);

    private static HttpClient Client(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://graph.facebook.com/")
    };

    private static WhatsAppOptions OptionsValue() => new()
    {
        Enabled = true,
        ApiVersion = "v22.0",
        PhoneNumberId = "phone-id",
        AccessToken = "meta-token",
        OperationsNumber = "+974 7445 2435",
        NewOrderTemplateName = "new_order_summary",
        TemplateLanguage = "en"
    };

    private static NewOrderWhatsAppNotification Notification() => new()
    {
        OrderId = Guid.NewGuid(),
        OrderNumber = "ORD-20260813-001",
        CustomerName = "Ahmed Ali",
        CustomerMobile = "+97450123456",
        MealPlanName = "Weight Loss Plan",
        Duration = "12 Days",
        MealsPerDay = 3,
        StartDate = new DateOnly(2026, 8, 15),
        DeliveryDays = "Sat, Sun, Mon, Tue, Wed, Thu",
        DeliveryAddress = "Apartment 1204, Doha, Qatar",
        TotalAmount = 499m,
        Currency = "QAR",
        OrderStatus = "CONFIRMED"
    };

    private static HttpResponseMessage MetaSuccess(string messageId) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $"{{\"messages\":[{{\"id\":\"{messageId}\"}}]}}",
            Encoding.UTF8,
            "application/json")
    };

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
