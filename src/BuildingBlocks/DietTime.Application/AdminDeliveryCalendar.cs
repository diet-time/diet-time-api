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
    Task<DeliveryPreparationSummaryResponse> GetPreparationSummaryAsync(
        DateOnly date,
        CancellationToken cancellationToken);
}

public interface IKitchenPreparationReportGenerator
{
    Task<byte[]> GenerateAsync(
        DeliveryPreparationSummaryResponse summary,
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

public sealed record DeliveryPreparationOrderSource(
    Guid OrderId,
    Guid CustomerId,
    Guid MealPlanId,
    string MealPlanName);

public sealed record DeliveryPreparationMenuSource(
    Guid OrderId,
    Guid MealTypeId,
    string MealTypeName,
    int MealTypeDisplayOrder,
    Guid MenuItemId,
    string MenuItemName,
    int Quantity);

public static class DeliveryPreparationAggregation
{
    public static DeliveryPreparationSummaryResponse Build(
        DateOnly date,
        IReadOnlyCollection<DeliveryPreparationOrderSource> orders,
        IReadOnlyCollection<DeliveryPreparationMenuSource> menuRows)
    {
        var mealTypes = menuRows
            .GroupBy(row => new
            {
                row.MealTypeId,
                row.MealTypeName,
                row.MealTypeDisplayOrder
            })
            .OrderBy(group => group.Key.MealTypeDisplayOrder)
            .ThenBy(group => group.Key.MealTypeName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var items = group
                    .GroupBy(row => new { row.MenuItemId, row.MenuItemName })
                    .Select(item => new DeliveryPreparationMenuItemResponse(
                        item.Key.MenuItemId,
                        item.Key.MenuItemName,
                        item.Sum(row => row.Quantity)))
                    .OrderByDescending(item => item.Quantity)
                    .ThenBy(item => item.MenuItemName, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return new DeliveryPreparationMealTypeResponse(
                    group.Key.MealTypeId,
                    group.Key.MealTypeName,
                    items.Sum(item => item.Quantity),
                    items);
            })
            .ToArray();

        var plans = orders
            .GroupBy(order => new { order.MealPlanId, order.MealPlanName })
            .Select(group => new DeliveryPreparationPlanResponse(
                group.Key.MealPlanId,
                group.Key.MealPlanName,
                group.Select(order => order.OrderId).Distinct().Count()))
            .OrderByDescending(plan => plan.OrderCount)
            .ThenBy(plan => plan.MealPlanName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new DeliveryPreparationSummaryResponse(
            date,
            orders.Count == 0 ? "NoDeliveries" : "Scheduled",
            orders.Select(order => order.OrderId).Distinct().Count(),
            orders.Select(order => order.CustomerId).Distinct().Count(),
            mealTypes.Sum(mealType => mealType.Quantity),
            mealTypes,
            plans);
    }
}
