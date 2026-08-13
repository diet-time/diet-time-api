using DietTime.Application;

namespace DietTime.UnitTests;

public sealed class OperationsDashboardTests
{
    [Fact]
    public void Recognizes_delivery_and_non_delivery_days()
    {
        var start = new DateOnly(2026, 8, 13);
        var end = new DateOnly(2026, 8, 20);

        Assert.True(OperationsDashboardScheduling.IsScheduled(start, start, end, [4, 6]));
        Assert.False(OperationsDashboardScheduling.IsScheduled(start.AddDays(1), start, end, [4, 6]));
        Assert.True(OperationsDashboardScheduling.IsScheduled(start.AddDays(2), start, end, [4, 6]));
    }

    [Fact]
    public void Finds_future_next_delivery_date()
    {
        var next = OperationsDashboardScheduling.NextScheduledDate(
            new DateOnly(2026, 8, 14),
            new DateOnly(2026, 8, 13),
            new DateOnly(2026, 8, 31),
            [4, 6]);

        Assert.Equal(new DateOnly(2026, 8, 15), next);
    }

    [Fact]
    public void Returns_none_for_active_period_without_a_valid_service_day()
    {
        var next = OperationsDashboardScheduling.NextScheduledDate(
            new DateOnly(2026, 8, 13),
            new DateOnly(2026, 8, 13),
            new DateOnly(2026, 8, 14),
            [6]);

        Assert.Null(next);
    }

    [Fact]
    public void Calculates_actual_last_scheduled_service_date()
    {
        var actual = OperationsDashboardScheduling.LastScheduledDate(
            new DateOnly(2026, 8, 13),
            new DateOnly(2026, 8, 16),
            [4, 6]);

        Assert.Equal(new DateOnly(2026, 8, 15), actual);
    }
}
