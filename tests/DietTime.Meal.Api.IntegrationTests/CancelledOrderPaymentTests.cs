using System.Net;
using System.Net.Http.Json;
using DietTime.Contracts;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Cancelled_order_rejects_payment_changes_without_altering_history_or_version()
    {
        var setup = await ApprovalClient();
        await using var factory = setup.Factory;
        using var client = setup.Client;
        var id = (await SubmitApprovalOrder(client, setup.Request)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/v1/orders/{id}/cancel", new OrderReviewRequest(1, "Customer cancelled"))).StatusCode);
        var before = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == id);
        var history = await db.OrderStatusHistory.CountAsync(h => h.OrderId == id);
        foreach (var target in new[] { "PAID", "FAILED", "REFUND_PENDING" })
        {
            var response = await client.PostAsJsonAsync($"/api/admin/orders/{id}/payment", new RecordOrderPaymentRequest(before.RowVersion, target, "Manual record"));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("cancelled_order_payment_locked", await response.Content.ReadAsStringAsync());
        }
        var after = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == id);
        Assert.Equal(before.PaymentStatus, after.PaymentStatus);
        Assert.Equal("CANCELLED", after.Status);
        Assert.Equal(before.RowVersion, after.RowVersion);
        Assert.Equal(history, await db.OrderStatusHistory.CountAsync(h => h.OrderId == id));
    }
}
