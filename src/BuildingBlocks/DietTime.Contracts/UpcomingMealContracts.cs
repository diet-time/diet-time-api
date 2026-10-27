namespace DietTime.Contracts;

public sealed record UpcomingMealDayResponse(Guid SubscriptionId, Guid SubscriptionServiceDayId,
    DateOnly DeliveryDate, int ServiceSequence, int MenuWeekNumber, string Status,
    bool CanEdit, DateTimeOffset? CutoffAt, string? LockReason)
{
    public Guid OrderId { get; init; }
    public string PlanName { get; init; } = "";
    public int MealCount { get; init; }
    public int SnackCount { get; init; }
    public string SelectionStatus { get; init; } = "DEFAULT_PENDING";
    public bool SelectionLocked { get; init; }
    public string DeliveryStatus { get; init; } = "PLANNED";
}
public sealed record UpcomingMealItemResponse(Guid MealItemId, Guid SlotOptionId,
    string MealName, string? Description, string? ImageUrl, decimal? Calories,
    string Category, Guid MealTypeId);
public sealed record AssignedMealResponse(Guid SelectionId, string SelectionSource,
    UpcomingMealItemResponse Meal)
{
    public int? SelectionNumber { get; init; }
}
public sealed record DailyMealMenuResponse(Guid SubscriptionId, Guid SubscriptionServiceDayId,
    DateOnly DeliveryDate, int MenuWeekNumber, int Weekday, int MealCount, int SnackCount,
    string Status, bool CanEdit, DateTimeOffset? CutoffAt, string? LockReason, long Revision,
    IReadOnlyList<AssignedMealResponse> SelectedMeals,
    IReadOnlyList<UpcomingMealItemResponse> AvailableAlternatives)
{
    public Guid OrderId { get; init; }
    public string SelectionStatus { get; init; } = "DEFAULT_PENDING";
    public bool SelectionLocked { get; init; }
    public string DeliveryStatus { get; init; } = "PLANNED";
}
public sealed record ReplaceAssignedMealRequest(Guid MealItemId, Guid SlotOptionId, long ExpectedRevision);

public sealed record ConfirmMealsRequest(IReadOnlyList<Guid> MealMenuItemIds, IReadOnlyList<Guid> SnackMenuItemIds);
public sealed record TomorrowMenuResponse(Guid SubscriptionId, Guid? SubscriptionServiceDayId,
    DateOnly DeliveryDate, string MealPlanName, int MealCount, int SnackCount,
    string SelectionStatus, bool SelectionLocked, bool CanConfirm, DateTimeOffset CutoffAt,
    DateTimeOffset ServerNow, string? ReviewReason, IReadOnlyList<Guid> SelectedMenuItemIds,
    IReadOnlyList<UpcomingMealItemResponse> SelectedMeals,
    IReadOnlyList<UpcomingMealItemResponse> AvailableMeals, IReadOnlyList<UpcomingMealItemResponse> AvailableSnacks)
{
    public string? OrderStatus { get; init; }
    public DateOnly? OrderStartDate { get; init; }
    public DateOnly? FirstDeliveryDate { get; init; }
    public Guid? MenuTemplateId { get; init; }
    public int? MenuTemplateVersion { get; init; }
    public int? MenuWeekNumber { get; init; }
    public string? EmptyReason { get; init; }
}
