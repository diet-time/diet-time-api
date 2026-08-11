using DietTime.Contracts;

namespace DietTime.Application;

public interface IAdminDeliveryCalendarService
{
    Task<AdminDeliveryCalendarResponse> GetMonthAsync(
        DateOnly startDate,
        DateOnly endDate,
        Guid? planId,
        string? orderStatus,
        CancellationToken cancellationToken);
}

public static class AdminDeliveryCalendarScheduling
{
    public static bool IsScheduled(
        DateOnly date,
        DateOnly startDate,
        DateOnly endDate,
        IReadOnlyCollection<int> deliveryDays) =>
        date >= startDate && date <= endDate &&
        deliveryDays.Contains(date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek);
}
