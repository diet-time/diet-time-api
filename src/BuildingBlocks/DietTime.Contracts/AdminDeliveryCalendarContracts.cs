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
    string Status);

public sealed record AdminDeliveryMealTypeTotalResponse(
    string MealType,
    int Quantity);

public sealed record AdminDeliveryCalendarDayResponse(
    DateOnly Date,
    int TotalOrders,
    int TotalCustomers,
    int TotalMealItems,
    IReadOnlyList<AdminDeliveryCalendarOrderResponse> Orders,
    IReadOnlyList<AdminDeliveryMealTypeTotalResponse> MealTypeTotals);

public sealed record AdminDeliveryCalendarResponse(
    DateOnly StartDate,
    DateOnly EndDate,
    IReadOnlyList<AdminDeliveryCalendarDayResponse> Days);
