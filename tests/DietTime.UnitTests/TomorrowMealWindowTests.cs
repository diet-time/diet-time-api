using DietTime.Application;

namespace DietTime.UnitTests;

public sealed class TomorrowMealWindowTests
{
    [Theory]
    [InlineData("2026-10-25T20:59:59Z", "2026-10-27", false)]
    [InlineData("2026-10-25T21:00:00Z", "2026-10-27", true)]
    [InlineData("2026-10-26T17:59:59.9999999Z", "2026-10-27", true)]
    [InlineData("2026-10-26T18:00:00Z", "2026-10-27", false)]
    [InlineData("2026-10-26T21:00:00Z", "2026-10-27", false)]
    [InlineData("2026-10-29T10:00:00Z", "2026-10-30", false)]
    [InlineData("2026-10-30T10:00:00Z", "2026-10-31", true)]
    [InlineData("2026-10-26T10:00:00-05:00", "2026-10-27", true)]
    public void Qatar_window_uses_server_instant_and_excludes_Friday(string instant, string date, bool expected) =>
        Assert.Equal(expected, SubscriptionMenuRules.IsSelectionOpen(DateOnly.Parse(date), DateTimeOffset.Parse(instant)));

    [Fact]
    public void Deadline_is_21_Qatar_18_UTC() => Assert.Equal(
        new DateTimeOffset(2026, 10, 26, 18, 0, 0, TimeSpan.Zero),
        SubscriptionMenuRules.TomorrowCutoff(new(2026, 10, 27)));
}
