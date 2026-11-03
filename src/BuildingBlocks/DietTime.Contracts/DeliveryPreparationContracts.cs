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
    public int? DeliveryOrderCount { get; init; }
    public int? TakeawayOrderCount { get; init; }
    public int TotalScheduledCount { get; init; }
    public int DeliveredCount { get; init; }
    public int CollectedCount { get; init; }
    public int RemainingCount { get; init; }
    public bool IsPreliminary { get; init; }
    public IReadOnlyList<string> OperationalExceptions { get; init; } = [];
    public IReadOnlyList<KitchenSelectionResponse> Selections { get; init; } = [];
}

public sealed record DeliveryPreparationMealTypeResponse(
    Guid MealTypeId,
    string MealTypeName,
    int Quantity,
    IReadOnlyList<DeliveryPreparationMenuItemResponse> Items)
{
    public string MealClassification { get; init; } = "meal";
    public int MealCategoryDisplayOrder { get; init; }
}

public sealed record DeliveryPreparationMenuItemResponse(
    Guid MenuItemId,
    string MenuItemName,
    int Quantity);

public sealed record DeliveryPreparationPlanResponse(
    Guid MealPlanId,
    string MealPlanName,
    int OrderCount);

public sealed record KitchenDishResponse(string MealName, string Category, int Quantity)
{
    public Guid? MealCategoryId { get; init; }
    public string? MealCategoryName { get; init; }
    public int MealCategoryDisplayOrder { get; init; } = int.MaxValue;
    public string MealClassification { get; init; } = Category;
}
public sealed record KitchenSelectionResponse(Guid ServiceDayId, string OrderReference, Guid CustomerReference,
    string FinalizationStatus, IReadOnlyList<string> SelectionSources, IReadOnlyList<KitchenDishResponse> Dishes,
    IReadOnlyList<string> DietaryRestrictions, string? ReviewReason,
    string FulfillmentMethod = "HOME_DELIVERY", string? TakeawayBranchName = null, string? TakeawayBranchAddress = null)
{
    public string? MealPlanName { get; init; }
    public IReadOnlyList<string>? FoodPreferences { get; init; }
    public IReadOnlyList<string>? Allergens { get; init; }
    public string? CustomerName { get; init; }
    public string? FulfillmentStatus { get; init; }
    public DateTimeOffset? FulfilledAt { get; init; }
    public Guid? FulfilledBy { get; init; }
    public string? DeliveryAddress { get; init; }
    public bool DeliveryAddressVerificationRequired { get; init; }
    public decimal? DeliveryLatitude { get; init; }
    public decimal? DeliveryLongitude { get; init; }
    public IReadOnlyList<string> DietaryRestrictionLabels { get; init; } = [];
}
