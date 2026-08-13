using DietTime.Contracts;

namespace DietTime.Application;

public interface IOperationsDashboardService
{
    DateOnly GetBusinessDate();
    Task<OperationsDashboardResponse> GetAsync(DateOnly date, CancellationToken cancellationToken);
    Task<DashboardDeliveriesPage> GetDeliveriesAsync(
        DateOnly date, int page, int pageSize, CancellationToken cancellationToken);
}

public static class OperationsDashboardScheduling
{
    public static bool IsScheduled(
        DateOnly date,
        DateOnly startDate,
        DateOnly endDate,
        IReadOnlyCollection<int> deliveryDays) =>
        date >= startDate && date <= endDate && deliveryDays.Contains(ToApiWeekday(date.DayOfWeek));

    public static DateOnly? NextScheduledDate(
        DateOnly fromDate,
        DateOnly startDate,
        DateOnly endDate,
        IReadOnlyCollection<int> deliveryDays)
    {
        if (deliveryDays.Count == 0 || endDate < fromDate)
            return null;

        var first = fromDate > startDate ? fromDate : startDate;
        for (var offset = 0; offset < 7 && first.AddDays(offset) <= endDate; offset++)
        {
            var candidate = first.AddDays(offset);
            if (deliveryDays.Contains(ToApiWeekday(candidate.DayOfWeek)))
                return candidate;
        }

        return null;
    }

    public static DateOnly? LastScheduledDate(
        DateOnly startDate,
        DateOnly endDate,
        IReadOnlyCollection<int> deliveryDays)
    {
        if (deliveryDays.Count == 0 || endDate < startDate)
            return null;

        for (var offset = 0; offset < 7 && endDate.AddDays(-offset) >= startDate; offset++)
        {
            var candidate = endDate.AddDays(-offset);
            if (deliveryDays.Contains(ToApiWeekday(candidate.DayOfWeek)))
                return candidate;
        }

        return null;
    }

    public static int ToApiWeekday(DayOfWeek day) => day == DayOfWeek.Sunday ? 7 : (int)day;
}
