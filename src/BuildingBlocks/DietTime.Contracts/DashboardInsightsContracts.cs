namespace DietTime.Contracts;
public sealed record DashboardInsightsDto(int ActiveSubscriptions,int PendingConfirmations,int ExpiringSubscriptions,
    DashboardFulfillmentDto Fulfillment,IReadOnlyList<DeliveryWorkloadDayDto> Workload,
    IReadOnlyList<DashboardPackageDto> PopularPackages,IReadOnlyList<DashboardSlotDto> DeliverySlots,
    IReadOnlyList<AdminDeliveryCalendarOrderResponse> ScheduledOrders);
public sealed record DashboardFulfillmentDto(int Total,int Pending,int Overdue,int Delivered,int Collected,int HomeDelivery,int Takeaway,int MealItems)
{
    public int Fulfilled => Delivered + Collected;
    public decimal? FulfillmentRate => Total>0 ? decimal.Round(100m*Fulfilled/Total,1) : null;
}
public sealed record DashboardPackageDto(string Name,int Subscriptions);
public sealed record DashboardSlotDto(string Name,TimeOnly? StartTime,TimeOnly? EndTime,int Scheduled,int Fulfilled,int Overdue);
