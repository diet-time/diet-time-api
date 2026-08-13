using System.Text.Json.Serialization;

namespace DietTime.Contracts;

public sealed record OperationsDashboardResponse(
    DateOnly Date,
    OperationsDashboardTodayDto Today,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] NextDeliveryDayDto? NextDeliveryDay,
    IReadOnlyList<DeliveryWorkloadDayDto> NextSevenDays,
    DashboardAttentionDto NeedsAttention,
    IReadOnlyList<DashboardDeliveryDto> TodayDeliveries,
    UpcomingPlanActivityDto UpcomingPlanActivity);

public sealed record OperationsDashboardTodayDto(
    int ScheduledDeliveries,
    int Customers,
    int MealsToPrepare,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? CompletedDeliveries);

public sealed record NextDeliveryDayDto(
    DateOnly Date,
    int ScheduledDeliveries,
    int Customers);

public sealed record DeliveryWorkloadDayDto(
    DateOnly Date,
    string DayName,
    int ScheduledDeliveries,
    int Customers,
    int Meals,
    bool HasDeliveries);

public sealed record DashboardAttentionDto(
    int MissingDeliveryAddresses,
    int OrdersRequiringReview,
    int PlansEndingSoon,
    int CustomersWithoutUpcomingDelivery,
    int DeliveryConflicts);

public sealed record DashboardDeliveryDto(
    Guid OrderId,
    string OrderNumber,
    Guid CustomerId,
    string CustomerName,
    Guid MealPlanId,
    string MealPlanName,
    int MealCount,
    string DeliveryArea,
    string DeliveryAddress,
    string Status);

public sealed record UpcomingPlanActivityDto(
    IReadOnlyList<PlanActivityDayDto> Starting,
    IReadOnlyList<PlanActivityDayDto> Ending);

public sealed record PlanActivityDayDto(DateOnly Date, int CustomerCount);

public sealed record DashboardDeliveriesPage(
    IReadOnlyList<DashboardDeliveryDto> Items,
    PaginationMeta Meta);
