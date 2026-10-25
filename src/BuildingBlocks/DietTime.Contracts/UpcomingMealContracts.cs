namespace DietTime.Contracts;

public sealed record UpcomingMealDayResponse(Guid SubscriptionId, Guid SubscriptionServiceDayId,
    DateOnly DeliveryDate, int ServiceSequence, int MenuWeekNumber, string Status,
    bool CanEdit, DateTimeOffset? CutoffAt, string? LockReason);
public sealed record UpcomingMealItemResponse(Guid MealItemId, Guid SlotOptionId,
    string MealName, string? Description, string? ImageUrl, decimal? Calories,
    string Category, Guid MealTypeId);
public sealed record AssignedMealResponse(Guid SelectionId, string SelectionSource,
    UpcomingMealItemResponse Meal);
public sealed record DailyMealMenuResponse(Guid SubscriptionId, Guid SubscriptionServiceDayId,
    DateOnly DeliveryDate, int MenuWeekNumber, int Weekday, int MealCount, int SnackCount,
    string Status, bool CanEdit, DateTimeOffset? CutoffAt, string? LockReason, long Revision,
    IReadOnlyList<AssignedMealResponse> SelectedMeals,
    IReadOnlyList<UpcomingMealItemResponse> AvailableAlternatives);
public sealed record ReplaceAssignedMealRequest(Guid MealItemId, Guid SlotOptionId, long ExpectedRevision);
