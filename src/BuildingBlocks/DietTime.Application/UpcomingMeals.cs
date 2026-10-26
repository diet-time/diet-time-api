using DietTime.Contracts;
using DietTime.Domain;

namespace DietTime.Application;

public interface IUpcomingMealService
{
    Task<TomorrowMenuResponse?> GetTomorrowAsync(Guid orderId, Guid userId, string language, CancellationToken ct);
    Task<TomorrowMenuResponse> ConfirmAsync(Guid orderId, Guid dayId, Guid userId, string language, ConfirmMealsRequest request, CancellationToken ct);
    Task FinalizeDueAsync(CancellationToken ct);
    Task InitializeAsync(Order order, CancellationToken ct);
    Task<IReadOnlyList<UpcomingMealDayResponse>?> GetDaysAsync(Guid orderId, Guid userId, CancellationToken ct);
    Task<DailyMealMenuResponse?> GetMenuAsync(Guid orderId, Guid dayId, Guid userId, string language, CancellationToken ct);
    Task<DailyMealMenuResponse> ReplaceAsync(Guid orderId, Guid dayId, Guid selectionId,
        Guid userId, string language, ReplaceAssignedMealRequest request, CancellationToken ct);
}

public sealed class UpcomingMealException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public static class SubscriptionMenuRules
{
    public static DateOnly QatarToday(DateTimeOffset now) => DateOnly.FromDateTime(
        TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById("Asia/Qatar")).DateTime);
    public static DateTimeOffset TomorrowCutoff(DateOnly deliveryDate) => QatarCutoff(deliveryDate, 1, new(21, 0));
    public static bool IsSelectionOpen(DateOnly deliveryDate, DateTimeOffset now) =>
        deliveryDate.DayOfWeek != DayOfWeek.Friday && deliveryDate == QatarToday(now).AddDays(1) && now < TomorrowCutoff(deliveryDate);

    public static int MenuWeek(int serviceSequence, int cycleWeeks)
    {
        if (serviceSequence < 1) throw new ArgumentOutOfRangeException(nameof(serviceSequence));
        if (cycleWeeks is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(cycleWeeks));
        return ((serviceSequence - 1) / 6) % cycleWeeks + 1;
    }

    public static IEnumerable<DateOnly> ServiceDates(DateOnly start, DateOnly end, IReadOnlySet<int> weekdays)
    {
        for (var date = start; date <= end; date = date.AddDays(1))
            if (date.DayOfWeek != DayOfWeek.Friday &&
                weekdays.Contains(date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek))
                yield return date;
    }

    public static DateTimeOffset QatarCutoff(DateOnly deliveryDate, int daysBefore, TimeOnly localTime)
    {
        if (daysBefore < 0) throw new ArgumentOutOfRangeException(nameof(daysBefore));
        var local = deliveryDate.AddDays(-daysBefore).ToDateTime(localTime, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local,
            TimeZoneInfo.FindSystemTimeZoneById("Asia/Qatar")), TimeSpan.Zero);
    }
}
