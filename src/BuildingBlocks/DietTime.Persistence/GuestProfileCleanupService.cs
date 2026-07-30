using DietTime.Application;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Persistence;

public sealed class GuestProfileCleanupService(
    DietTimeDbContext db,
    GuestProfileOptions options,
    TimeProvider clock) : IGuestProfileCleanupService
{
    public async Task<int> DeleteExpiredBatchAsync(CancellationToken ct)
    {
        var batchSize = Math.Clamp(options.CleanupBatchSize, 1, 5000);
        var threshold = clock.GetUtcNow().AddDays(-Math.Max(0, options.ExpiredProfileRetentionDays));
        var ids = await db.CustomerProfiles.AsNoTracking()
            .Where(x =>
                x.UserId == null &&
                x.GuestTokenHash != null &&
                x.GuestTokenExpiresAt < threshold)
            .OrderBy(x => x.GuestTokenExpiresAt)
            .Select(x => x.Id)
            .Take(batchSize)
            .ToArrayAsync(ct);
        if (ids.Length == 0)
            return 0;

        return await db.CustomerProfiles
            .Where(x => ids.Contains(x.Id) && x.UserId == null)
            .ExecuteDeleteAsync(ct);
    }
}
