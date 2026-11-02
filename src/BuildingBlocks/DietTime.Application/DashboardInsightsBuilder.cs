using DietTime.Contracts;
namespace DietTime.Application;
public sealed record DashboardSubscriptionSource(Guid OrderId,DateOnly StartDate,DateOnly EndDate,int Meals,int Snacks,DateOnly? LastServiceDate);
public sealed record DashboardSlotSource(Guid OrderId,string Name,TimeOnly? StartTime,TimeOnly? EndTime);
public static class DashboardInsightsBuilder
{
    public static DashboardInsightsDto Build(DateOnly date,IReadOnlyList<AdminDeliveryCalendarDayResponse> days,
        IReadOnlyList<DashboardSubscriptionSource> subscriptions,IReadOnlyList<DashboardSlotSource> slots,int pendingConfirmations,int rowLimit=10)
    {
        var day=days.FirstOrDefault(d=>d.Date==date);
        var rows=day?.Orders ?? [];
        var current=subscriptions.Where(s=>s.StartDate<=date && s.EndDate>=date).DistinctBy(s=>s.OrderId).ToArray();
        var end=date.AddDays(6);
        var expiring=current.Count(s=>s.LastServiceDate>=date && s.LastServiceDate<=end);
        var fulfillment=new DashboardFulfillmentDto(day?.TotalServiceDays ?? 0,day?.PendingCount ?? 0,day?.OverdueCount ?? 0,
            day?.DeliveredCount ?? 0,day?.CollectedCount ?? 0,rows.Count(r=>r.FulfillmentMethod=="HOME_DELIVERY"),rows.Count(r=>r.FulfillmentMethod=="TAKEAWAY"),day?.TotalMealItems ?? 0);
        var workload=Enumerable.Range(0,7).Select(offset=> {
            var serviceDate=date.AddDays(offset);var item=days.FirstOrDefault(d=>d.Date==serviceDate);
            return new DeliveryWorkloadDayDto(serviceDate,serviceDate.DayOfWeek.ToString(),item?.TotalServiceDays ?? 0,item?.TotalCustomers ?? 0,item?.TotalMealItems ?? 0,item?.TotalServiceDays>0);
        }).ToArray();
        var packages=current.GroupBy(s=>new {s.Meals,s.Snacks})
            .Select(g=>new DashboardPackageDto($"{g.Key.Meals} {(g.Key.Meals==1 ? "meal" : "meals")}"+
                (g.Key.Snacks>0 ? $" + {g.Key.Snacks} {(g.Key.Snacks==1 ? "snack" : "snacks")}" : "")+" / day",g.Count()))
            .OrderByDescending(p=>p.Subscriptions).ThenBy(p=>p.Name).ToArray();
        var slotLookup=slots.DistinctBy(s=>s.OrderId).ToDictionary(s=>s.OrderId);
        var groupedSlots=rows.Where(r=>r.FulfillmentMethod=="HOME_DELIVERY").Select(r=>new {
            Row=r,Slot=slotLookup.GetValueOrDefault(r.Id) ?? new DashboardSlotSource(r.Id,"Slot not recorded",null,null)
        }).GroupBy(r=>new {r.Slot.Name,r.Slot.StartTime,r.Slot.EndTime})
            .Select(g=>new DashboardSlotDto(g.Key.Name,g.Key.StartTime,g.Key.EndTime,g.Count(),g.Count(r=>r.Row.FulfillmentStatus is "DELIVERED" or "COLLECTED"),g.Count(r=>r.Row.IsOverdue)))
            .OrderBy(s=>s.StartTime).ThenBy(s=>s.Name).ToArray();
        return new(current.Length,pendingConfirmations,expiring,fulfillment,workload,packages,groupedSlots,rows.Take(Math.Max(0,rowLimit)).ToArray());
    }
}
