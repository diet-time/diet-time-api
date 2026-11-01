using DietTime.Application;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
namespace DietTime.Persistence;

// Capture only real business events, atomically in the same SaveChanges/transaction.
// Placement, approval/payment policies and fulfillment transition logic are unchanged.
internal static class StaffNotificationCapture
{
    public static void Capture(DietTimeDbContext db)
    {
        db.ChangeTracker.DetectChanges();
        var events = new List<StaffNotificationEvent>();
        foreach (var entry in db.ChangeTracker.Entries<OrderStatusHistory>().Where(e => e.State == EntityState.Added).ToArray()) {
            var h = entry.Entity;
            if (StaffNotificationRules.OrderEvent(h.PreviousStatus,h.Status) is not { } type) continue;
            events.Add(new() { Id=Guid.NewGuid(), DeduplicationKey=$"history:{h.Id}", EventType=type, OrderId=h.OrderId, CreatedAt=h.ChangedAt });
        }
        foreach (var entry in db.ChangeTracker.Entries<SubscriptionServiceDay>().Where(e => e.State == EntityState.Modified).ToArray()) {
            var day=entry.Entity;
            if (day.OrderId is not { } order || day.FulfilledAt is not { } at || day.Status is not ("DELIVERED" or "COLLECTED") ||
                entry.Property(d=>d.Status).OriginalValue == day.Status) continue;
            events.Add(new() { Id=Guid.NewGuid(), DeduplicationKey=$"fulfillment:{day.Id}:{day.Status}", EventType=day.Status, OrderId=order, ServiceDayId=day.Id, ServiceDate=day.ServiceDate, CreatedAt=at });
        }
        var tracked=db.ChangeTracker.Entries<StaffNotificationEvent>().Select(e=>e.Entity.DeduplicationKey).ToHashSet();
        db.StaffNotificationEvents.AddRange(events.Where(e=>tracked.Add(e.DeduplicationKey)));
    }
}
