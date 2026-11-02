using DietTime.Application;

namespace DietTime.UnitTests;

public sealed class BookingCutoffTests
{
    [Theory]
    [InlineData("2026-10-26T17:59:59Z", 1, "2026-10-27")]
    [InlineData("2026-10-26T18:00:00Z", 1, "2026-10-28")]
    [InlineData("2026-10-26T18:00:01Z", 1, "2026-10-28")]
    [InlineData("2026-10-26T21:00:00+03:00", 1, "2026-10-28")]
    [InlineData("2026-10-26T20:59:59Z", 1, "2026-10-28")]
    [InlineData("2026-10-26T21:00:00Z", 1, "2026-10-28")]
    [InlineData("2026-10-26T18:00:00Z", 3, "2026-10-29")]
    [InlineData("2026-10-26T17:00:00Z", 0, "2026-10-27")]
    public void Booking_minimum_uses_the_Qatar_cutoff_and_preserves_longer_lead_times(
        string instant, int lead, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), OrderSchedulingRules.MinimumStartDate(DateTimeOffset.Parse(instant), lead));
}