using DietTime.Application;
using DietTime.Contracts;
namespace DietTime.UnitTests;
public sealed class DashboardInsightsTests
{
    private static readonly DateOnly Date = new(2026, 11, 2);
    private static AdminDeliveryCalendarOrderResponse Row(string mode, string status, bool overdue = false) =>
        new(Guid.NewGuid(), "ORD-1", Guid.NewGuid(), "Customer", Guid.NewGuid(), "Balance", 3, "Morning", "ACTIVE", mode)
        { FulfillmentStatus = status, IsOverdue = overdue };
    [Fact]
    public void Counts_use_all_service_days_before_display_limit()
    {
        var rows = new[] { Row("HOME_DELIVERY", "DELIVERED"), Row("TAKEAWAY", "COLLECTED"), Row("HOME_DELIVERY", "SCHEDULED", true), Row("HOME_DELIVERY", "SCHEDULED") };
        var day = new AdminDeliveryCalendarDayResponse(Date, 4, 4, 12, rows, [])
        { TotalServiceDays = 4, DeliveredCount = 1, CollectedCount = 1, PendingCount = 1, OverdueCount = 1 };
        var slots = rows.Select(r => new DashboardSlotSource(r.Id, "Morning", new(7, 0), new(11, 0))).ToArray();
        var result = DashboardInsightsBuilder.Build(Date, [day], [], slots, 3, 1);
        Assert.Single(result.ScheduledOrders);
        Assert.Equal(4, result.Fulfillment.Total);
        Assert.Equal(50m, result.Fulfillment.FulfillmentRate);
        Assert.Equal(3, result.Fulfillment.HomeDelivery);
        Assert.Equal(1, result.Fulfillment.Takeaway);
        Assert.Equal(12, result.Fulfillment.MealItems);
        var slot = Assert.Single(result.DeliverySlots);
        Assert.Equal(3, slot.Scheduled);
        Assert.Equal(1, slot.Fulfilled);
        Assert.Equal(1, slot.Overdue);
        Assert.Equal(new TimeOnly(7, 0), slot.StartTime);
        Assert.Equal(3, result.PendingConfirmations);
    }
    [Fact]
    public void Packages_and_renewals_count_current_distinct_subscriptions()
    {
        var first = new DashboardSubscriptionSource(Guid.NewGuid(), Date.AddDays(-10), Date.AddDays(6), 2, 1, Date.AddDays(6));
        var current = first with { OrderId = Guid.NewGuid(), EndDate = Date.AddDays(7), LastServiceDate = Date.AddDays(7) };
        var future = first with { OrderId = Guid.NewGuid(), StartDate = Date.AddDays(1) };
        var ended = first with { OrderId = Guid.NewGuid(), EndDate = Date.AddDays(-1) };
        var result = DashboardInsightsBuilder.Build(Date, [], [first, first, current, future, ended], [], 0);
        Assert.Equal(2, result.ActiveSubscriptions);
        Assert.Equal(1, result.ExpiringSubscriptions);
        var package = Assert.Single(result.PopularPackages);
        Assert.Equal("2 meals + 1 snack / day", package.Name);
        Assert.Equal(2, package.Subscriptions);
    }
    [Fact]
    public void Empty_days_have_no_invented_rate_or_workload()
    {
        var result = DashboardInsightsBuilder.Build(Date, [], [], [], 0);
        Assert.Null(result.Fulfillment.FulfillmentRate);
        Assert.Empty(result.DeliverySlots);
        Assert.Empty(result.ScheduledOrders);
        Assert.Equal(7, result.Workload.Count);
        Assert.Equal(Date.AddDays(6), result.Workload[^1].Date);
        Assert.All(result.Workload, day => { Assert.Equal(0, day.Meals); Assert.Equal(0, day.ScheduledDeliveries); Assert.False(day.HasDeliveries); });
    }
    [Fact]
    public void Missing_slot_window_remains_unknown()
    {
        var row = Row("HOME_DELIVERY", "SCHEDULED");
        var day = new AdminDeliveryCalendarDayResponse(Date, 1, 1, 3, [row], []) { TotalServiceDays = 1, PendingCount = 1 };
        var slot = Assert.Single(DashboardInsightsBuilder.Build(Date, [day], [], [], 0).DeliverySlots);
        Assert.Equal("Slot not recorded", slot.Name);
        Assert.Null(slot.StartTime);
        Assert.Null(slot.EndTime);
    }
}
