using DietTime.Application;

namespace DietTime.UnitTests;

public sealed class AdminDeliveryCalendarTests
{
    [Theory]
    [InlineData("2026-08-09", true)]
    [InlineData("2026-08-10", true)]
    [InlineData("2026-08-11", false)]
    public void Matches_api_weekdays(string value, bool expected)
    {
        var date = DateOnly.Parse(value);

        var scheduled = AdminDeliveryCalendarScheduling.IsScheduled(
            date, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), [1, 7]);

        Assert.Equal(expected, scheduled);
    }

    [Fact]
    public void Excludes_matching_weekdays_outside_the_order_period()
    {
        var scheduled = AdminDeliveryCalendarScheduling.IsScheduled(
            new DateOnly(2026, 9, 6), new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), [7]);

        Assert.False(scheduled);
    }
}
