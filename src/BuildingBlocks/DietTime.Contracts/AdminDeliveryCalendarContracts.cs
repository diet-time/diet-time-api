namespace DietTime.Contracts;

public sealed record AdminDeliveryCalendarOrderResponse(
    Guid Id,
    string OrderNumber,
    Guid CustomerProfileId,
    string CustomerName,
    Guid MealPlanTemplateId,
    string PlanName,
    int MealCount,
    string DeliverySlot,
    string Status, string FulfillmentMethod = "HOME_DELIVERY", string? TakeawayBranchName = null, string? TakeawayBranchAddress = null)
{
    public Guid? ServiceDayId { get; init; }
    public string? FulfillmentStatus { get; init; }
    public DateTimeOffset? FulfilledAt { get; init; }
    public Guid? FulfilledBy { get; init; }
    public DateOnly ServiceDate { get; init; }
    public DateTimeOffset? FulfillmentDeadline { get; init; }
    public bool IsOverdue { get; init; }
    public string? StaffDisplayName { get; init; }
    public string? DeliveryAddress { get; init; }
}

public sealed record AdminDeliveryMealTypeTotalResponse(
    string MealType,
    int Quantity);

public sealed record AdminDeliveryCalendarDayResponse(
    DateOnly Date,
    int TotalOrders,
    int TotalCustomers,
    int TotalMealItems,
    IReadOnlyList<AdminDeliveryCalendarOrderResponse> Orders,
    IReadOnlyList<AdminDeliveryMealTypeTotalResponse> MealTypeTotals)
{
    public int OrderCount => TotalOrders;
    public int CustomerCount => TotalCustomers;
    public int TotalServiceDays { get; init; }
    public int PendingCount { get; init; }
    public int OverdueCount { get; init; }
    public int DeliveredCount { get; init; }
    public int CollectedCount { get; init; }
    public int FulfilledCount => DeliveredCount + CollectedCount;
    public string FulfillmentProgress { get; init; } = "NO_DELIVERIES";
}

public sealed record AdminDeliveryCalendarResponse(
    DateOnly StartDate,
    DateOnly EndDate,
    IReadOnlyList<AdminDeliveryCalendarDayResponse> Days);
