using DietTime.Application;
using DietTime.Domain;

namespace DietTime.UnitTests;
public sealed class DailyFulfillmentTrackingTests
{
    private static Order Home() => new() { FulfillmentMethod = "HOME_DELIVERY", DeliveryTimeSlotName = "Morning", DeliveryStartTime = new(7, 0), DeliveryEndTime = new(11, 0) };
    [Theory]
    [InlineData("2026-11-02T07:30:00Z", false)] // 10:30 Qatar
    [InlineData("2026-11-02T08:00:00Z", false)] // exact deadline
    [InlineData("2026-11-02T08:01:00Z", true)]
    [InlineData("2026-11-03T08:01:00Z", true)]
    [InlineData("2026-11-01T22:01:00Z", false)] // date rollover in Qatar, still before deadline
    public void Morning_overdue_uses_Qatar_slot_end(string time, bool expected)
    {
        var date = new DateOnly(2026,11,2);
        var deadline = DailyFulfillmentTracking.Deadline(date, Home(), null);
        Assert.Equal(DateTimeOffset.Parse("2026-11-02T08:00:00Z"), deadline);
        Assert.Equal(expected, DailyFulfillmentTracking.IsOverdue(date, "PLANNED", deadline, DateTimeOffset.Parse(time)));
    }
    [Theory]
    [InlineData("2026-11-02T14:00:00Z", false)]
    [InlineData("2026-11-02T15:01:00Z", true)]
    public void Configured_evening_window_overrides_order_snapshot(string now, bool expected)
    {
        var date = new DateOnly(2026,11,2);
        var deadline = DailyFulfillmentTracking.Deadline(date, Home(), new DeliveryTimeSlot { StartTime = new(16,0), EndTime = new(18,0) });
        Assert.Equal(expected, DailyFulfillmentTracking.IsOverdue(date, "SCHEDULED", deadline, DateTimeOffset.Parse(now)));
    }
    [Theory]
    [InlineData("DELIVERED")]
    [InlineData("COLLECTED")]
    [InlineData("CANCELLED")]
    [InlineData("SKIPPED")]
    public void Resolved_or_ineligible_days_are_never_overdue(string status)
    {
        Assert.False(DailyFulfillmentTracking.IsOverdue(new(2026,11,2), status, DateTimeOffset.Parse("2026-11-02T08:00Z"), DateTimeOffset.Parse("2026-11-05T09:00Z")));
    }
    [Fact]
    public void Takeaway_without_collection_configuration_has_no_deadline()
    {
        var order = Home(); order.FulfillmentMethod = "TAKEAWAY";
        var deadline = DailyFulfillmentTracking.Deadline(new(2026,11,2), order, new DeliveryTimeSlot { StartTime = new(7,0), EndTime = new(11,0) });
        Assert.Null(deadline);
        Assert.False(DailyFulfillmentTracking.IsOverdue(new(2026,11,2), "SCHEDULED", deadline, DateTimeOffset.Parse("2026-12-01T09:00Z")));
    }
    [Fact]
    public void Future_service_day_is_never_overdue_even_with_bad_deadline_data()
    {
        Assert.False(DailyFulfillmentTracking.IsOverdue(new(2026,11,3), "SCHEDULED", DateTimeOffset.Parse("2026-11-01T08:00Z"), DateTimeOffset.Parse("2026-11-02T08:00Z")));
    }
    [Fact]
    public void Missing_delivery_window_is_not_invented()
    {
        Assert.Null(DailyFulfillmentTracking.Deadline(new(2026,11,2), new Order(), null));
    }
    [Fact]
    public void Overnight_configured_slot_ends_on_following_Qatar_day()
    {
        Assert.Equal(DateTimeOffset.Parse("2026-11-02T23:00Z"), DailyFulfillmentTracking.Deadline(new(2026,11,2), Home(), new DeliveryTimeSlot { StartTime = new(22,0), EndTime = new(2,0) }));
    }
    [Fact]
    public void Calendar_priorities_and_partition_are_consistent()
    {
        Assert.Equal("NO_DELIVERIES", DailyFulfillmentTracking.Summarize([]).Status);
        Assert.Equal("SCHEDULED", DailyFulfillmentTracking.Summarize([new("SCHEDULED",false)]).Status);
        Assert.Equal("PARTIALLY_FULFILLED", DailyFulfillmentTracking.Summarize([new("DELIVERED",false),new("SCHEDULED",false)]).Status);
        Assert.Equal("FULFILLED", DailyFulfillmentTracking.Summarize([new("DELIVERED",false),new("COLLECTED",false)]).Status);
        var overdue = DailyFulfillmentTracking.Summarize([new("DELIVERED",false),new("COLLECTED",false),new("SCHEDULED",true),new("SCHEDULED",false)]);
        Assert.Equal("OVERDUE", overdue.Status);
        Assert.Equal(4, overdue.TotalServiceDays);
        Assert.Equal(overdue.TotalServiceDays, overdue.PendingCount + overdue.OverdueCount + overdue.FulfilledCount);
        Assert.Equal(1, overdue.DeliveredCount); Assert.Equal(1, overdue.CollectedCount);
    }
    [Fact]
    public void Eligibility_excludes_friday_rejected_skipped_and_failed_payment()
    {
        var order = Home(); order.Id = Guid.NewGuid(); order.Status = "CONFIRMED";
        order.StartDate = new(2026,11,1); order.EndDate = new(2026,11,30);
        order.DeliveryDays.Add(new OrderDeliveryDay { DayOfWeek = 1 });
        order.DeliveryDays.Add(new OrderDeliveryDay { DayOfWeek = 5 });
        var day = new SubscriptionServiceDay { OrderId = order.Id, ServiceDate = new(2026,11,2) };
        Assert.True(DailyFulfillmentTracking.IsEligible(day,order));
        day.ServiceDate = new(2026,11,6); Assert.False(DailyFulfillmentTracking.IsEligible(day,order));
        day.ServiceDate = new(2026,11,2); day.Status = "SKIPPED"; Assert.False(DailyFulfillmentTracking.IsEligible(day,order));
        day.Status = "SCHEDULED"; order.Status = "REJECTED"; Assert.False(DailyFulfillmentTracking.IsEligible(day,order));
        order.Status = "CONFIRMED"; order.PaymentStatus = "FAILED"; Assert.False(DailyFulfillmentTracking.IsEligible(day,order));
    }
}
