namespace DietTime.Contracts;

public sealed record DeliveryPreparationSummaryResponse(
    DateOnly Date,
    string Status,
    int OrderCount,
    int CustomerCount,
    int MealItemCount,
    IReadOnlyList<DeliveryPreparationMealTypeResponse> MealTypes,
    IReadOnlyList<DeliveryPreparationPlanResponse> PlanBreakdown)
{
    public bool IsPreliminary { get; init; }
    public IReadOnlyList<string> OperationalExceptions { get; init; } = [];
    public IReadOnlyList<KitchenSelectionResponse> Selections { get; init; } = [];
}

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

public sealed record KitchenDishResponse(string MealName, string Category, int Quantity);
public sealed record KitchenSelectionResponse(Guid ServiceDayId, string OrderReference, Guid CustomerReference,
    string FinalizationStatus, IReadOnlyList<string> SelectionSources, IReadOnlyList<KitchenDishResponse> Dishes,
    IReadOnlyList<string> DietaryRestrictions, string? ReviewReason,
    string FulfillmentMethod = "HOME_DELIVERY", string? TakeawayBranchName = null, string? TakeawayBranchAddress = null);
