namespace DietTime.Contracts;

public sealed record DeliveryPreparationSummaryResponse(
    DateOnly Date,
    string Status,
    int OrderCount,
    int CustomerCount,
    int MealItemCount,
    IReadOnlyList<DeliveryPreparationMealTypeResponse> MealTypes,
    IReadOnlyList<DeliveryPreparationPlanResponse> PlanBreakdown);

public sealed record DeliveryPreparationMealTypeResponse(
    Guid MealTypeId,
    string MealTypeName,
    int Quantity,
    IReadOnlyList<DeliveryPreparationMenuItemResponse> Items);

public sealed record DeliveryPreparationMenuItemResponse(
    Guid MenuItemId,
    string MenuItemName,
    int Quantity);

public sealed record DeliveryPreparationPlanResponse(
    Guid MealPlanId,
    string MealPlanName,
    int OrderCount);
