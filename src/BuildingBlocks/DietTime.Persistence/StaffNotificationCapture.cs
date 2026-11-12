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
        foreach (var entry in db.ChangeTracker.Entries<PackageChangeRequest>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified).ToArray()) {
            var request = entry.Entity;
            if (request.Type is not ("PAUSE" or "RESUME")) continue;
            var requested = entry.State == EntityState.Added && request.Status == "PENDING_APPROVAL";
            var reviewed = entry.State == EntityState.Modified &&
                entry.Property(r => r.Status).OriginalValue == "PENDING_APPROVAL" &&
                request.Status is "APPROVED" or "REJECTED" && request.ReviewedAt.HasValue;
            if (!requested && !reviewed) continue;
            var action = requested ? "REQUESTED" : request.Status;
            events.Add(new() { Id = Guid.NewGuid(), DeduplicationKey = $"package-request:{request.Id}:{action}",
                EventType = $"PACKAGE_{request.Type}_{action}", OrderId = request.OrderId,
                CreatedAt = requested ? request.CreatedAt : request.ReviewedAt!.Value });
        }
        var tracked=db.ChangeTracker.Entries<StaffNotificationEvent>().Select(e=>e.Entity.DeduplicationKey).ToHashSet();
        db.StaffNotificationEvents.AddRange(events.Where(e=>tracked.Add(e.DeduplicationKey)));
    }
}
