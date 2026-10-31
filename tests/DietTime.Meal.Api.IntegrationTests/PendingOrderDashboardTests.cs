using System.Net.Http.Json;
using System.Text.Json;
using DietTime.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Pending_orders_are_visible_to_staff_and_counted_even_without_service_days()
    {
        var setup = await ApprovalClient();
        await using var factory = setup.Factory;
        using var client = setup.Client;
        var submitted = await SubmitApprovalOrder(client, setup.Request);
        var id = submitted.GetProperty("id").GetGuid();

        var list = await client.GetFromJsonAsync<JsonElement>("/api/admin/orders?status=PENDING_APPROVAL");
        Assert.Equal(1, list.GetProperty("totalCount").GetInt32());
        Assert.Equal(id, Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("order").GetProperty("id").GetGuid());
        Assert.False(await db.Set<DietTime.Domain.SubscriptionServiceDay>().AnyAsync(day => day.OrderId == id));

        // Pending reviews are a global queue, independent of the selected dispatch date.
        var dashboard = await client.GetFromJsonAsync<JsonElement>("/api/admin/dashboard/operations?date=2030-01-01");
        Assert.Equal(1, dashboard.GetProperty("needsAttention").GetProperty("ordersRequiringReview").GetInt32());
        Assert.Equal(0, dashboard.GetProperty("today").GetProperty("scheduledDeliveries").GetInt32());

        // Older deployments configured an empty array; it must no longer suppress new orders.
        var service = new OperationsDashboardService(db, clock,
            Options.Create(new OperationsDashboardOptions { ReviewOrderStatuses = [] }));
        var legacyConfig = await service.GetAsync(new(2030, 1, 1), default);
        Assert.Equal(1, legacyConfig.NeedsAttention.OrdersRequiringReview);

        (await Review(client, id, "approve", 1)).EnsureSuccessStatusCode();
        list = await client.GetFromJsonAsync<JsonElement>("/api/admin/orders?status=PENDING_APPROVAL");
        Assert.Equal(0, list.GetProperty("totalCount").GetInt32());
        dashboard = await client.GetFromJsonAsync<JsonElement>("/api/admin/dashboard/operations?date=2030-01-01");
        Assert.Equal(0, dashboard.GetProperty("needsAttention").GetProperty("ordersRequiringReview").GetInt32());
    }
}
