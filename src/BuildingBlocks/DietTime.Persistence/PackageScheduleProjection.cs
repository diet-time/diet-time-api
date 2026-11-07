using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Persistence;

internal static class PackageScheduleProjection
{
    public static async Task<IReadOnlyDictionary<Guid, PackageScheduleSummaryResponse>> LoadAsync(
        DietTimeDbContext db, IReadOnlyCollection<Order> orders, DateTimeOffset now, CancellationToken ct)
    {
        var ids = orders.Select(o => o.Id).ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, PackageScheduleSummaryResponse>();
        var today = SubscriptionMenuRules.QatarToday(now);
        var days = await db.Set<SubscriptionServiceDay>().AsNoTracking().Where(d => d.OrderId != null && ids.Contains(d.OrderId.Value)).ToArrayAsync(ct);
        var pauses = await db.Set<SubscriptionPause>().AsNoTracking().Where(p => ids.Contains(p.OrderId) && p.Status == "CONFIRMED" && p.ResumeDate > today).ToArrayAsync(ct);
        var changes = await db.Set<PackageChangeRequest>().AsNoTracking().Where(r => ids.Contains(r.OrderId)).ToArrayAsync(ct);
        return orders.ToDictionary(o => o.Id, o => SubscriptionScheduleRules.Summary(o,
            days.Where(d => d.OrderId == o.Id).ToArray(),
            pauses.Where(p => p.OrderId == o.Id).OrderByDescending(p => p.CreatedAt).FirstOrDefault(),
            changes.Where(r => r.OrderId == o.Id).OrderByDescending(r => r.Status == "PENDING_APPROVAL").ThenByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).FirstOrDefault(), now));
    }
}
