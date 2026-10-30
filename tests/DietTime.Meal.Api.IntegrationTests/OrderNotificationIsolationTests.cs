using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Slow_and_failed_Twilio_cannot_block_order_response_or_dashboard_orders()
    {
        clock.Now = new(2026, 10, 26, 10, 0, 0, TimeSpan.Zero);
        var slot = new DeliveryTimeSlot { Id = Guid.NewGuid(), Code = "AM", Name = "Morning", NameAr = "Morning", StartTime = new(7, 0), EndTime = new(11, 0), IsActive = true };
        var address = new CustomerAddress { Id = Guid.NewGuid(), CustomerProfileId = order.CustomerProfileId, AddressType = "HOME", Area = "Doha", IsActive = true };
        db.AddRange(slot, address);
        await db.SaveChangesAsync();
        var provider = new SlowFailingTwilio();
        await using var factory = new ApiFactory(connection, services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<ITwilioWhatsAppService>(provider);
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Development-User-Id", userId.ToString());
        var price = await db.MealPlanPrices.SingleAsync();
        var request = new PlaceOrderRequest(order.CustomerProfileId, order.MealPlanTemplateId, price.Id,
            address.Id, slot.Id, new(2026, 10, 27), [1, 2, 3, 4, 6, 7]);
        var key = Guid.NewGuid().ToString();
        async Task<HttpResponseMessage> Submit()
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders") { Content = JsonContent.Create(request) };
            message.Headers.Add("Idempotency-Key", key);
            return await client.SendAsync(message);
        }
        try
        {
            // Before this fix, placement waits indefinitely for this fake provider.
            var response = await Submit().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(provider.Release.Task.IsCompleted);
            var placed = await response.Content.ReadFromJsonAsync<JsonElement>();
            var orderId = placed.GetProperty("id").GetGuid();
            var summary = await client.GetFromJsonAsync<JsonElement>($"/api/v1/customer-profiles/{order.CustomerProfileId}/orders");
            Assert.Contains(summary.GetProperty("items").EnumerateArray(), x => x.GetProperty("id").GetGuid() == orderId);
            var replay = await Submit().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());
            Assert.Equal(1, provider.Calls);
            provider.Release.SetResult(new WhatsAppSendResult { Success = false, ErrorCode = "63016", Status = "undelivered" });
            var details = await client.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{orderId}");
            Assert.Equal("PENDING_APPROVAL", details.GetProperty("status").GetString());
        }
        finally { provider.Release.TrySetResult(new WhatsAppSendResult { Success = false, ErrorCode = "test_cleanup" }); }
    }

    private sealed class SlowFailingTwilio : ITwilioWhatsAppService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<WhatsAppSendResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public async Task<WhatsAppSendResult> SendNewOrderNotificationAsync(NewOrderWhatsAppNotification notification, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            return await Release.Task.WaitAsync(cancellationToken);
        }
        public Task<WhatsAppMessageStatusResult> GetMessageStatusAsync(string messageSid, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WhatsAppSendResult> SendTemplateAsync(TwilioWhatsAppTemplateMessage message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
