using DietTime.Application;
using DietTime.Domain;
namespace DietTime.UnitTests;

public sealed class PackagePauseRulesTests
{
    [Theory]
    [InlineData("2026-11-03", true)]
    [InlineData("2027-05-02", true)]
    [InlineData("2027-05-03", false)]
    [InlineData("2026-11-02", false)]
    public void Restart_date_is_bounded_to_six_calendar_months(string date, bool valid)
    {
        if (valid) PackagePauseRules.ValidateResumeDate(DateOnly.Parse(date), Now);
        else Assert.Equal(400, Assert.Throws<UpcomingMealException>(() => PackagePauseRules.ValidateResumeDate(DateOnly.Parse(date), Now)).StatusCode);
    }

    [Fact]
    public void Six_month_limit_clamps_month_end_and_uses_Qatar_date()
    {
        Assert.Equal(new DateOnly(2027, 2, 28), PackagePauseRules.MaximumResumeDate(DateTimeOffset.Parse("2026-08-30T21:00:00Z")));
        Assert.Equal(new DateOnly(2026, 11, 4), PackagePauseRules.MinimumResumeDate(DateTimeOffset.Parse("2026-11-02T18:00:00Z")));
    }
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
    private static SubscriptionServiceDay Day(int offset) => new() { Id = Guid.NewGuid(), ServiceDate = new DateOnly(2026, 11, 2).AddDays(offset) };

    [Theory]
    [InlineData(-1, false)] [InlineData(0, false)] [InlineData(1, true)] [InlineData(4, false)] [InlineData(5, true)]
    public void Pause_only_accepts_future_non_Friday_days(int offset, bool expected) => Assert.Equal(expected, PackagePauseRules.CanMove(Day(offset), Now));

    [Theory]
    [InlineData("DELIVERED")] [InlineData("COLLECTED")] [InlineData("PREPARING")] [InlineData("OUT_FOR_DELIVERY")]
    [InlineData("SKIPPED")] [InlineData("CANCELLED")]
    public void Committed_and_non_service_states_never_move(string status) {
        var day = Day(1); day.Status = status; Assert.False(PackagePauseRules.CanMove(day, Now));
    }
    [Fact]
    public void Every_lock_or_fulfillment_marker_is_independently_enforced() {
        var day = Day(1); day.SelectionLocked = true; Assert.False(PackagePauseRules.CanMove(day, Now));
        day = Day(1); day.SelectionStatus = "LOCKED"; Assert.False(PackagePauseRules.CanMove(day, Now));
        day = Day(1); day.CustomerConfirmedAt = Now; Assert.False(PackagePauseRules.CanMove(day, Now));
        day = Day(1); day.AutoFinalizedAt = Now; Assert.False(PackagePauseRules.CanMove(day, Now));
        day = Day(1); day.FulfilledAt = Now; Assert.False(PackagePauseRules.CanMove(day, Now));
        day = Day(1); day.SelectionCutoffAt = Now; Assert.False(PackagePauseRules.CanMove(day, Now));
    }
    [Fact]
    public void Qatar_midnight_and_exact_21_cutoff_are_authoritative() {
        var day = Day(1); var cutoff = SubscriptionMenuRules.TomorrowCutoff(day.ServiceDate);
        Assert.True(PackagePauseRules.CanMove(day, cutoff.AddTicks(-1)));
        Assert.False(PackagePauseRules.CanMove(day, cutoff));
        Assert.False(PackagePauseRules.CanMove(day, new(2026, 11, 2, 21, 0, 0, TimeSpan.Zero))); // Nov 3 in Qatar.
        Assert.Equal(new DateOnly(2026, 11, 4), PackagePauseRules.MinimumResumeDate(cutoff));
    }
    [Theory]
    [InlineData("CONFIRMED", "PAID", true)] [InlineData("ACTIVE", "PAID", true)] [InlineData("PAUSED", "PAID", true)]
    [InlineData("CONFIRMED", "PENDING", false)] [InlineData("CONFIRMED", "UNPAID", false)]
    [InlineData("PENDING_APPROVAL", "PENDING", false)] [InlineData("PENDING_APPROVAL", "PAID", false)]
    [InlineData("CANCELLED", "PAID", false)]
    public void Approval_and_payment_are_separate_conditions(string approval, string payment, bool blocked) {
        var order = new Order { Status = approval, PaymentStatus = payment };
        Assert.Equal(blocked, PackagePauseRules.CancellationBlocked(order));
        Assert.Equal(payment, order.PaymentStatus); Assert.Equal(approval, order.Status);
    }
    [Fact]
    public void Schedule_preserves_IDs_skips_Friday_selected_weekdays_and_occupied_records() {
        var a = Day(1); var b = Day(2); var locked = Day(5); locked.SelectionLocked = true;
        var original = new[] { a.ServiceDate, b.ServiceDate, locked.ServiceDate };
        var schedule = PackagePauseRules.Schedule([a,b], [a,b,locked], new(2026,11,6), new HashSet<int>{1,2,3,4,6,7});
        Assert.Equal(new DateOnly(2026,11,8), schedule[a.Id]); // Friday excluded; Saturday occupied.
        Assert.Equal(new DateOnly(2026,11,9), schedule[b.Id]);
        Assert.Equal(new[] {a.Id,b.Id}.Order(), schedule.Keys.Order());
        Assert.Equal(original, new[] {a.ServiceDate,b.ServiceDate,locked.ServiceDate});
        Assert.Equal(2, schedule.Values.Distinct().Count());
    }
    [Fact]
    public void Resume_schedule_moves_the_same_entitlement_back_without_using_a_fulfilled_date() {
        var delivered = Day(1); delivered.Status = "DELIVERED"; var a=Day(8); var b=Day(9);
        var schedule = PackagePauseRules.Schedule([a,b], [delivered,a,b], new(2026,11,3), new HashSet<int>{2,4});
        Assert.Equal(new DateOnly(2026,11,5), schedule[a.Id]); Assert.Equal(new DateOnly(2026,11,10), schedule[b.Id]);
        Assert.Equal("DELIVERED", delivered.Status); Assert.DoesNotContain(delivered.Id, schedule.Keys);
    }
}
