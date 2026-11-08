using DietTime.Application;
using DietTime.Domain;

namespace DietTime.UnitTests;

public sealed class DashboardPlanEligibilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
    private static Order Plan(string status = "CONFIRMED") {
        var order = new Order { Status = status, PaymentStatus = "PENDING", RequiresPrepayment = true,
            StartDate = new(2026, 11, 9), EndDate = new(2026, 11, 16) };
        foreach (var weekday in new[] { 1, 2, 3, 4, 6, 7 }) order.DeliveryDays.Add(new() { DayOfWeek = weekday });
        return order;
    }

    [Theory]
    [InlineData("CONFIRMED", "PENDING", true, "ACTIVE")]
    [InlineData("CONFIRMED", "PAID", true, "ACTIVE")]
    [InlineData("ACTIVE", "PENDING", true, "ACTIVE")]
    [InlineData("PAUSED", "PAID", true, "PAUSED")]
    [InlineData("PENDING_APPROVAL", "PAID", false, "PENDING_APPROVAL")]
    [InlineData("PENDING", "PAID", false, "PENDING_APPROVAL")]
    [InlineData("CANCELLED", "PAID", false, "CANCELLED")]
    [InlineData("COMPLETED", "PAID", false, "COMPLETED")]
    [InlineData("REJECTED", "PAID", false, "REJECTED")]
    [InlineData("ON_HOLD", "PAID", false, "ON_HOLD")]
    public void Visibility_is_separate_from_payment_and_fulfillment(string status, string payment, bool eligible, string state) {
        var order = Plan(status); order.PaymentStatus = payment;
        var result = DashboardPlanEligibility.Evaluate(order, [], null, null, Now);
        Assert.Equal(eligible, result.Eligible); Assert.Equal(state, result.Status);
        if (eligible) Assert.True(result.RemainingServiceDays > 0);
        if (payment == "PENDING") Assert.False(OrderWorkflowPolicy.CanFulfill(order));
    }

    [Theory]
    [InlineData(20, 59, "UPCOMING")]
    [InlineData(21, 0, "ACTIVE")]
    public void Today_boundary_uses_Qatar_not_UTC(int hour, int minute, string status) {
        var order = Plan();
        var result = DashboardPlanEligibility.Evaluate(order, [], null, null, new(2026, 11, 8, hour, minute, 0, TimeSpan.Zero));
        Assert.True(result.Eligible); Assert.Equal(status, result.Status);
    }

    [Fact]
    public void End_date_is_inclusive_and_expired_period_is_excluded() {
        var order = Plan(); order.EndDate = order.StartDate;
        Assert.True(DashboardPlanEligibility.Evaluate(order, [], null, null, Now).Eligible);
        Assert.Equal("SERVICE_PERIOD_ENDED", DashboardPlanEligibility.Evaluate(order, [], null, null, Now.AddDays(1)).ExclusionReason);
    }

    [Fact]
    public void Persisted_closures_Fridays_and_consumed_services_never_become_remaining_days() {
        var days = new SubscriptionServiceDay[] {
            new() { ServiceDate = new(2026, 11, 9), Status = "DELIVERED" },
            new() { ServiceDate = new(2026, 11, 10), Status = "SKIPPED" },
            new() { ServiceDate = new(2026, 11, 11), Status = "CANCELLED" },
            new() { ServiceDate = new(2026, 11, 12), Status = "DISPLACED" },
            new() { ServiceDate = new(2026, 11, 13), Status = "CONFIRMED" },
            new() { ServiceDate = new(2026, 11, 14), Status = "COLLECTED" }
        };
        var result = DashboardPlanEligibility.Evaluate(Plan(), days, null, null, Now);
        Assert.False(result.Eligible); Assert.Equal("NO_REMAINING_SERVICE_DAYS", result.ExclusionReason);
        Assert.Equal("PERSISTED_SERVICE_DAYS", result.ScheduleSource);
    }

    [Theory]
    [InlineData(9, "PAUSED")]
    [InlineData(10, "PAUSE_SCHEDULED")]
    public void Approved_pause_does_not_hide_the_subscription(int start, string expected) {
        var pause = new SubscriptionPause { Status = "CONFIRMED", PauseStartDate = new(2026, 11, start), ResumeDate = new(2026, 11, 15) };
        var result = DashboardPlanEligibility.Evaluate(Plan(), [new() { ServiceDate = new(2026, 11, 16), Status = "PLANNED" }], pause, null, Now);
        Assert.True(result.Eligible); Assert.Equal(expected, result.Status); Assert.Equal(1, result.RemainingServiceDays);
    }
}
