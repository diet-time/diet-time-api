using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Terminal_order_dashboard_preserves_payment_history_and_cancels_future_services()
    {
        var setup = await ApprovalClient();
        await using var factory = setup.Factory;
        using var client = setup.Client;
        foreach (var payment in new[] { "PENDING", "PAID", "REFUND_PENDING", "PARTIALLY_REFUNDED", "REFUNDED" })
        {
            clock.Now = clock.Now.AddSeconds(1); // Deterministic recent-history ordering across purchases.
            var id = (await SubmitApprovalOrder(client, setup.Request)).GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.OK, (await Review(client, id, "approve", 1)).StatusCode);
            long version = 2;
            var states = payment == "PENDING" ? Array.Empty<string>() :
                payment == "PAID" ? new[] { "PAID" } :
                payment == "REFUND_PENDING" ? new[] { "PAID", "REFUND_PENDING" } :
                new[] { "PAID", "REFUND_PENDING", payment };
            foreach (var state in states)
            {
                Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(
                    $"/api/admin/orders/{id}/payment", new RecordOrderPaymentRequest(version++, state, "Verified transaction"))).StatusCode);
            }
            var historyBefore = await db.OrderStatusHistory.AsNoTracking().Where(h => h.OrderId == id).ToArrayAsync();
            var selectionsBefore = await db.Set<CustomerMealSelection>().CountAsync(s => s.ServiceDay.OrderId == id);
            Assert.Equal(HttpStatusCode.OK, (await Review(client, id, "cancel", version, "Staff cancellation")).StatusCode);
            var saved = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == id);
            Assert.Equal("CANCELLED", saved.Status);
            Assert.Equal(payment == "PAID" ? "REFUND_PENDING" : payment, saved.PaymentStatus);
            foreach (var entry in historyBefore)
            {
                var retained = await db.OrderStatusHistory.AsNoTracking().SingleAsync(h => h.Id == entry.Id);
                Assert.Equal(entry.PaymentStatus, retained.PaymentStatus);
                Assert.Equal(entry.PreviousPaymentStatus, retained.PreviousPaymentStatus);
                Assert.Equal(entry.Notes, retained.Notes);
            }
            Assert.Equal(selectionsBefore, await db.Set<CustomerMealSelection>().CountAsync(s => s.ServiceDay.OrderId == id));
            Assert.False(await db.Set<SubscriptionServiceDay>().AnyAsync(d => d.OrderId == id && d.Status != "CANCELLED"));
            var days = (await client.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{id}/upcoming-meals")).GetProperty("data");
            Assert.Empty(days.EnumerateArray());
            var tomorrow = (await client.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{id}/tomorrow-menu")).GetProperty("data");
            Assert.False(tomorrow.GetProperty("canConfirm").GetBoolean());
            var preparation = await new AdminDeliveryCalendarService(db, Options.Create(new MenuRotationOptions()), clock)
                .GetPreparationSummaryAsync(saved.StartDate, default);
            Assert.DoesNotContain(preparation.Selections, s => s.OrderReference == saved.OrderNumber);
            Assert.False(OrderWorkflowPolicy.CanFulfill(saved));
            var dashboard = await client.GetFromJsonAsync<CustomerOrdersResponse>(
                $"/api/v1/customer-profiles/{saved.CustomerProfileId}/orders/dashboard");
            var summary = Assert.Single(dashboard!.Items.Where(o => o.Id == id));
            Assert.Equal(saved.PaymentStatus, summary.PaymentStatus);
            Assert.False(summary.Review!.AwaitingPayment);
            Assert.False(summary.Review.CanFulfill);
            Assert.Equal(0, summary.PackageSchedule!.RemainingServiceDays);
            Assert.Null(summary.PackageSchedule.NextScheduledDelivery);
            Assert.False(summary.PackageSchedule.TomorrowScheduled);
        }
    }

    [UpcomingPostgresFact]
    public async Task Rejection_cancels_legacy_future_service_rows_without_deleting_meal_history()
    {
        var setup = await ApprovalClient();
        await using var factory = setup.Factory;
        using var client = setup.Client;
        var id = (await SubmitApprovalOrder(client, setup.Request)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Review(client, id, "approve", 1)).StatusCode);
        // Simulate an imported pending order that already has provisional service rows.
        await db.Orders.Where(o => o.Id == id).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, "PENDING_APPROVAL"));
        var count = await db.Set<CustomerMealSelection>().CountAsync(s => s.ServiceDay.OrderId == id);
        Assert.Equal(HttpStatusCode.OK, (await Review(client, id, "reject", 2, "Unable to supply")).StatusCode);
        Assert.True(await db.Set<SubscriptionServiceDay>().AnyAsync(d => d.OrderId == id));
        Assert.False(await db.Set<SubscriptionServiceDay>().AnyAsync(d => d.OrderId == id && d.Status != "CANCELLED"));
        Assert.Equal(count, await db.Set<CustomerMealSelection>().CountAsync(s => s.ServiceDay.OrderId == id));
    }
}
