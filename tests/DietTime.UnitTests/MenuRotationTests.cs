using DietTime.Application;

namespace DietTime.UnitTests;

public sealed class MenuRotationTests
{
    private static readonly DateOnly Anchor = new(2026, 1, 3); // Saturday

    [Fact]
    public void Maps_the_first_six_delivery_days_to_week_one()
    {
        Assert.Equal(1, MenuRotationResolver.ResolveWeek(Anchor, 4, null, Anchor));
        Assert.Equal(1, MenuRotationResolver.ResolveWeek(new(2026, 1, 8), 4, null, Anchor));
    }

    [Fact]
    public void Skips_friday_without_advancing_the_rotation()
    {
        Assert.Null(MenuRotationResolver.ResolveWeek(new(2026, 1, 9), 4, null, Anchor));
        Assert.Equal(2, MenuRotationResolver.ResolveWeek(new(2026, 1, 10), 4, null, Anchor));
    }

    [Fact]
    public void Maps_days_seven_through_twenty_four_to_weeks_two_through_four()
    {
        Assert.Equal(2, MenuRotationResolver.ResolveWeek(new(2026, 1, 15), 4, null, Anchor));
        Assert.Equal(3, MenuRotationResolver.ResolveWeek(new(2026, 1, 22), 4, null, Anchor));
        Assert.Equal(4, MenuRotationResolver.ResolveWeek(new(2026, 1, 29), 4, null, Anchor));
    }

    [Fact]
    public void Week_five_repeats_week_one()
    {
        Assert.Equal(1, MenuRotationResolver.ResolveWeek(new(2026, 1, 31), 4, null, Anchor));
    }

    [Fact]
    public void Valid_from_overrides_the_default_preview_anchor()
    {
        Assert.Null(MenuRotationResolver.ResolveWeek(new(2026, 1, 3), 4, new(2026, 1, 10), Anchor));
        Assert.Equal(1, MenuRotationResolver.ResolveWeek(new(2026, 1, 10), 4, new(2026, 1, 10), Anchor));
    }
}
