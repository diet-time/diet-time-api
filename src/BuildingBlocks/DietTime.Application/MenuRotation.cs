using DietTime.Domain;

namespace DietTime.Application;

public sealed class MenuRotationOptions
{
    public const string SectionName = "MenuRotation";

    // This is the calendar anchor used by previews when a published template has
    // no ValidFrom date. It is deliberately configuration-driven so operations can
    // align the public preview with the business cycle without using a subscription
    // sequence number.
    public DateOnly AnchorDate { get; set; } = new(2024, 1, 6);
}

public static class MenuRotationResolver
{
    private static readonly MenuWeekday[] DeliveryWeekdays =
    [
        MenuWeekday.Saturday,
        MenuWeekday.Sunday,
        MenuWeekday.Monday,
        MenuWeekday.Tuesday,
        MenuWeekday.Wednesday,
        MenuWeekday.Thursday
    ];

    public static int? ResolveWeek(
        DateOnly date,
        int cycleWeeks,
        DateOnly? validFrom,
        DateOnly anchorDate)
    {
        if (cycleWeeks is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(cycleWeeks), "Menu cycles must contain between one and four weeks.");

        var cycleStart = FirstDeliveryDate(validFrom ?? anchorDate);
        if (date < cycleStart || !IsDeliveryDate(date))
            return null;

        var span = date.DayNumber - cycleStart.DayNumber;
        var completeSevenDayBlocks = span / 7;
        var remainder = span % 7;
        var deliveryDay = completeSevenDayBlocks * DeliveryWeekdays.Length;

        for (var offset = 0; offset <= remainder; offset++)
        {
            if (IsDeliveryDate(cycleStart.AddDays(completeSevenDayBlocks * 7 + offset)))
                deliveryDay++;
        }

        return ((deliveryDay - 1) / DeliveryWeekdays.Length) % cycleWeeks + 1;
    }

    public static DateOnly FirstDeliveryDate(DateOnly date)
    {
        while (!IsDeliveryDate(date))
            date = date.AddDays(1);
        return date;
    }

    public static bool IsDeliveryDate(DateOnly date) =>
        DeliveryWeekdays.Contains(MenuWeekdayExtensions.FromDate(date));
}
