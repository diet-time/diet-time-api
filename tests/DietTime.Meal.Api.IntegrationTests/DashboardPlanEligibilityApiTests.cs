using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Dashboard_visibility_includes_approved_unpaid_future_and_current_plans_without_unlocking_fulfillment()
    {
        var setup = await ApprovalClient(true, services => services.PostConfigure<OrderOptions>(options => options.EnableDashboardDiagnostics = true));
        await using var factory = setup.Factory;
        using var client = setup.Client;
        var id = (await SubmitApprovalOrder(client, setup.Request)).GetProperty("id").GetGuid();
        var saved = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == id);
        var path = $"/api/v1/customer-profiles/{saved.CustomerProfileId}/orders/dashboard";
        async Task<CustomerOrderSummaryResponse> Read() =>
            Assert.Single((await client.GetFromJsonAsync<CustomerOrdersResponse>(path))!.Items.Where(o => o.Id == id));
        var pending = await Read();
        Assert.False(pending.PackageSchedule!.DashboardEligible);
        Assert.Equal("PENDING_APPROVAL", pending.PackageSchedule.DashboardStatus);
        Assert.Equal("AWAITING_APPROVAL", pending.PackageSchedule.DashboardExclusionReason);
        (await Review(client, id, "approve", 1)).EnsureSuccessStatusCode();
        var upcoming = await Read();
        Assert.Equal("CONFIRMED", upcoming.Status);
        Assert.Equal("PENDING", upcoming.PaymentStatus);
        Assert.False(upcoming.Review!.CanFulfill); // The old Flutter predicate excluded this approved order.
        Assert.True(upcoming.PackageSchedule!.DashboardEligible);
        Assert.Equal("UPCOMING", upcoming.PackageSchedule.DashboardStatus);
        Assert.Equal(12, upcoming.PackageSchedule.RemainingServiceDays);
        Assert.False(upcoming.PackageSchedule.TomorrowScheduled);
        var management = await client.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{id}/package-management");
        Assert.Equal(upcoming.PackageSchedule.RemainingServiceDays, management.GetProperty("remainingServiceDays").GetInt32());
        Assert.False(await db.Set<SubscriptionServiceDay>().AnyAsync(d => d.OrderId == id));
        Console.WriteLine("DASHBOARD_BEFORE_AFTER " + JsonSerializer.Serialize(new {
            upcoming.Id, upcoming.Status, upcoming.PaymentStatus,
            beforeVisible = upcoming.Review.CanFulfill,
            afterVisible = upcoming.PackageSchedule.DashboardEligible,
            afterState = upcoming.PackageSchedule.DashboardStatus,
            remainingServiceDays = upcoming.PackageSchedule.RemainingServiceDays
        }));
        clock.Now = new(2026, 10, 27, 0, 0, 0, TimeSpan.FromHours(3));
        var current = await Read();
        Assert.True(current.PackageSchedule!.DashboardEligible);
        Assert.Equal("ACTIVE", current.PackageSchedule.DashboardStatus);
        Assert.False(current.Review!.CanFulfill);
        var otherProfile = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/customer-profiles/{otherProfile}/orders/dashboard")).StatusCode);
        clock.Now = new(saved.EndDate.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3));
        var expired = await Read();
        Assert.False(expired.PackageSchedule!.DashboardEligible);
        Assert.Equal("SERVICE_PERIOD_ENDED", expired.PackageSchedule.DashboardExclusionReason);
    }

    [UpcomingPostgresFact]
    public async Task Dashboard_considers_all_owned_approved_plans_beyond_recent_history_and_keeps_paused_subscriptions()
    {
        var setup = await ApprovalClient();
        await using var factory = setup.Factory;
        using var client = setup.Client;
        var id = (await SubmitApprovalOrder(client, setup.Request)).GetProperty("id").GetGuid();
        (await Review(client, id, "approve", 1)).EnsureSuccessStatusCode();
        var saved = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == id);
        db.Add(new SubscriptionPause { Id = Guid.NewGuid(), OrderId = id, Status = "CONFIRMED",
            PauseStartDate = new(2026, 10, 26), ResumeDate = new(2026, 10, 29), CreatedAt = clock.Now });
        await db.SaveChangesAsync();
        for (var i = 0; i < 4; i++) {
            clock.Now = clock.Now.AddSeconds(1);
            var rejected = (await SubmitApprovalOrder(client, setup.Request)).GetProperty("id").GetGuid();
            (await Review(client, rejected, "reject", 1, "Unavailable")).EnsureSuccessStatusCode();
        }
        var data = await client.GetFromJsonAsync<CustomerOrdersResponse>($"/api/v1/customer-profiles/{saved.CustomerProfileId}/orders/dashboard");
        var included = Assert.Single(data!.Items.Where(o => o.Id == id));
        Assert.True(included.PackageSchedule!.DashboardEligible);
        Assert.Equal("PAUSED", included.PackageSchedule.DashboardStatus);
        Assert.NotNull(included.PackageSchedule.ResumeDate);
        Assert.All(data.Items.Where(o => o.Status == "REJECTED"), o => Assert.False(o.PackageSchedule!.DashboardEligible));
    }
}
