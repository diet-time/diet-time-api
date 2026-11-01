using DietTime.Application;
namespace DietTime.UnitTests;
public sealed class OrderApprovalFilterTests
{
    [Fact]
    public void Pending_confirmation_includes_legacy_pending_without_fulfilled_states() =>
        Assert.Equal(new[] { "PENDING_APPROVAL", "PENDING" }, OrderApprovalFilter.MatchingStatuses("PENDING_APPROVAL"));
    [Fact]
    public void Confirmed_includes_active_and_historical_completed_orders() =>
        Assert.Equal(new[] { "CONFIRMED", "ACTIVE", "COMPLETED" }, OrderApprovalFilter.MatchingStatuses("CONFIRMED"));
    [Theory]
    [InlineData("ON_HOLD")]
    [InlineData("REJECTED")]
    [InlineData("CANCELLED")]
    [InlineData("ACTIVE")]
    [InlineData("PENDING")]
    public void Other_and_legacy_explicit_queries_remain_exact(string status) =>
        Assert.Equal(new[] { status }, OrderApprovalFilter.MatchingStatuses(status));
}
