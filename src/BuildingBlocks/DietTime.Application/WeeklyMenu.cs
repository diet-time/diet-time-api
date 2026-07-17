using DietTime.Contracts;
using DietTime.Domain;

namespace DietTime.Application;

public sealed class DeliveryScheduleOptions
{
    public const string SectionName = "DeliverySchedule";
    public string[] ActiveWeekdays { get; set; } =
    [
        "SATURDAY", "SUNDAY", "MONDAY", "TUESDAY", "WEDNESDAY", "THURSDAY"
    ];

    public bool IsDeliveryDay(MenuWeekday weekday) => ActiveWeekdays.Any(value =>
        value.Equals(weekday.Code(), StringComparison.OrdinalIgnoreCase));
}

public sealed class TemplateDayException(string code, string message, int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed class DefaultOperationalCalendarService : IOperationalCalendarService
{
    public Task<bool> IsOperationalDateAsync(DateOnly date, CancellationToken cancellationToken) => Task.FromResult(true);
}

public sealed class DeliverySchedulingService(
    IOperationalCalendarService operationalCalendar,
    ITemplateMenuReader mealPlans) : IDeliverySchedulingService
{
    public async Task<DateOnly> GetNextDeliveryDateAsync(
        DateOnly joiningDate,
        IReadOnlyCollection<DayOfWeek> activeDeliveryDays,
        IReadOnlyCollection<DateOnly> holidays,
        CancellationToken cancellationToken)
    {
        if (activeDeliveryDays.Count == 0)
            throw new ArgumentException("At least one active delivery weekday is required.", nameof(activeDeliveryDays));

        var holidaySet = holidays.ToHashSet();
        for (var offset = 1; offset <= 3660; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = joiningDate.AddDays(offset);
            if (!activeDeliveryDays.Contains(candidate.DayOfWeek) || holidaySet.Contains(candidate)) continue;
            if (await operationalCalendar.IsOperationalDateAsync(candidate, cancellationToken)) return candidate;
        }

        throw new InvalidOperationException("No operational delivery date was found within the supported scheduling horizon.");
    }

    public async Task<MealPlanTemplateDayDetailResponse> GetMenuForDeliveryDateAsync(
        Guid templateId,
        DateOnly deliveryDate,
        CancellationToken cancellationToken)
    {
        var weekday = MenuWeekdayExtensions.FromDate(deliveryDate);
        return await mealPlans.GetTemplateDayByWeekdayAsync(templateId, weekday, cancellationToken)
            ?? throw new TemplateDayException(
                "MENU_NOT_CONFIGURED_FOR_WEEKDAY",
                $"No {weekday} menu is configured for this template.",
                404);
    }
}
