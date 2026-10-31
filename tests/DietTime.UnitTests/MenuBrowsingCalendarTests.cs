using DietTime.Application;
using DietTime.Domain;

namespace DietTime.UnitTests;

public sealed class MenuBrowsingCalendarTests
{
    // Explicit test configuration, not the deployed template's assumed start date.
    private static readonly DateOnly Start = new(2026, 11, 1);

    [Theory]
    [InlineData("2026-11-01", 1, MenuWeekday.Sunday)]
    [InlineData("2026-11-02", 1, MenuWeekday.Monday)]
    [InlineData("2026-11-06", 1, MenuWeekday.Friday)]
    [InlineData("2026-11-07", 1, MenuWeekday.Saturday)]
    [InlineData("2026-11-08", 2, MenuWeekday.Sunday)]
    [InlineData("2026-11-15", 3, MenuWeekday.Sunday)]
    [InlineData("2026-11-22", 4, MenuWeekday.Sunday)]
    [InlineData("2026-11-29", 1, MenuWeekday.Sunday)]
    [InlineData("2026-12-06", 2, MenuWeekday.Sunday)]
    [InlineData("2027-01-03", 2, MenuWeekday.Sunday)]
    public void Browsing_uses_seven_day_weeks_from_the_saved_template_start(string date, int week, MenuWeekday weekday)
    {
        var selected = DateOnly.Parse(date);
        Assert.Equal(week, MenuRotationResolver.ResolveCalendarWeek(selected, 4, Start, new(2024, 1, 6)));
        Assert.Equal(weekday, MenuWeekdayExtensions.FromDate(selected));
    }

    [Fact]
    public void Browsing_uses_the_configured_fallback_and_never_invents_a_week_before_the_anchor()
    {
        var anchor = new DateOnly(2026, 12, 25); // Friday: remains the actual calendar anchor.
        Assert.Equal(1, MenuRotationResolver.ResolveCalendarWeek(anchor, 4, null, anchor));
        Assert.Equal(2, MenuRotationResolver.ResolveCalendarWeek(new(2027, 1, 1), 4, null, anchor));
        Assert.Null(MenuRotationResolver.ResolveCalendarWeek(anchor.AddDays(-1), 4, null, anchor));
        Assert.Equal(1, MenuRotationResolver.ResolveCalendarWeek(anchor.AddDays(28), 4, null, anchor));
        Assert.Throws<ArgumentOutOfRangeException>(() => MenuRotationResolver.ResolveCalendarWeek(anchor, 0, null, anchor));
    }
}
