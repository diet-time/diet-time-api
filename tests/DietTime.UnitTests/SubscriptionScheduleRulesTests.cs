using DietTime.Application;
using DietTime.Domain;

namespace DietTime.UnitTests;

public sealed class SubscriptionScheduleRulesTests
{
    private static Order Order() {
        var order = new Order { Status = "ACTIVE", PaymentStatus = "PAID", StartDate = new(2026, 11, 1), EndDate = new(2026, 12, 1) };
        foreach (var weekday in new[] { 1, 2, 3, 4, 6, 7 }) order.DeliveryDays.Add(new OrderDeliveryDay { DayOfWeek = weekday });
        return order;
    }
    private static SubscriptionPause Pause() => new() { Status = "CONFIRMED", PauseStartDate = new(2026, 11, 8), ResumeDate = new(2026, 11, 12) };

    [Theory]
    [InlineData(7, "PAUSE_SCHEDULED")]
    [InlineData(8, "PAUSED")]
    [InlineData(11, "PAUSED")]
    [InlineData(12, "ACTIVE")]
    public void Approved_pause_display_uses_effective_dates(int day, string expected) =>
        Assert.Equal(expected, SubscriptionScheduleRules.Summary(Order(), [], Pause(), null, new(2026, 11, day, 10, 0, 0, TimeSpan.Zero)).DisplayStatus);

    [Fact]
    public void Pending_pause_does_not_change_the_actual_delivery_schedule() {
        var day = new SubscriptionServiceDay { ServiceDate = new(2026, 11, 8), Status = "PLANNED" };
        var result = SubscriptionScheduleRules.Summary(Order(), [day], null,
            new() { Type = "PAUSE", Status = "PENDING_APPROVAL", PauseStartDate = day.ServiceDate, ResumeDate = new(2026, 11, 12) }, new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero));
        Assert.Equal("PAUSE_REQUESTED", result.DisplayStatus); Assert.True(result.TomorrowScheduled);
        Assert.Equal(day.ServiceDate, result.NextScheduledDelivery);
    }

    [Theory]
    [InlineData("PAUSED")]
    [InlineData("SUSPENDED")]
    [InlineData("CANCELLED")]
    [InlineData("DISPLACED")]
    [InlineData("SKIPPED")]
    [InlineData("DELIVERED")]
    public void Locked_selections_do_not_make_inactive_service_days_scheduled(string status) =>
        Assert.False(SubscriptionScheduleRules.IsScheduled(Order(), new() { ServiceDate = new(2026, 11, 8), Status = status, SelectionLocked = true, CustomerConfirmedAt = DateTimeOffset.UtcNow }));

    [Fact]
    public void Preserved_committed_day_remains_valid_during_an_approved_pause() {
        var result = SubscriptionScheduleRules.Summary(Order(), [new() { ServiceDate = new(2026, 11, 9), Status = "CONFIRMED", SelectionLocked = true }],
            Pause(), null, new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
        Assert.Equal("PAUSED", result.DisplayStatus); Assert.True(result.TomorrowScheduled);
    }

    [Fact]
    public void Fridays_and_unconfigured_weekdays_are_excluded() {
        var order = Order();
        Assert.False(SubscriptionScheduleRules.IsScheduled(order, new() { ServiceDate = new(2026, 11, 13), Status = "CONFIRMED" }));
        order.DeliveryDays.Clear(); order.DeliveryDays.Add(new() { DayOfWeek = 1 });
        Assert.False(SubscriptionScheduleRules.IsScheduled(order, new() { ServiceDate = new(2026, 11, 8), Status = "CONFIRMED" }));
    }

    [Fact]
    public void Cancelled_and_resumed_states_override_obsolete_pause_history() {
        var order = Order(); order.Status = "CANCELLED";
        Assert.Equal("CANCELLED", SubscriptionScheduleRules.Summary(order, [], Pause(), null, new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero)).DisplayStatus);
        order.Status = "ACTIVE"; var resumed = Pause(); resumed.Status = "RESUMED";
        Assert.Equal("ACTIVE", SubscriptionScheduleRules.Summary(order, [], resumed, null, new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero)).DisplayStatus);
    }

    [Theory]
    [InlineData("CANCELLED")]
    [InlineData("REJECTED")]
    [InlineData("COMPLETED")]
    public void Terminal_orders_never_expose_remaining_or_upcoming_services(string status)
    {
        var order = Order(); order.Status = status;
        var day = new SubscriptionServiceDay {
            ServiceDate = new(2026, 11, 9), Status = "CONFIRMED",
            SelectionLocked = true, CustomerConfirmedAt = DateTimeOffset.UtcNow
        };
        var result = SubscriptionScheduleRules.Summary(order, [day], Pause(), null,
            new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
        Assert.Equal(status, result.DisplayStatus);
        Assert.Equal(0, result.RemainingServiceDays);
        Assert.Null(result.NextScheduledDelivery);
        Assert.False(result.TomorrowScheduled);
        Assert.Equal("CONFIRMED", day.Status);
        Assert.True(day.SelectionLocked);
    }
}
