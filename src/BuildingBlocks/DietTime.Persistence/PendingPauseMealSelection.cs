using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Persistence;

public sealed partial class UpcomingMealService
{
    private const string PendingPauseSelectionMessage = "Meal selection is on hold for this date while your pause request awaits Diet Time approval.";

    // A pending request restricts customer edits, without changing delivery dates or commitments.
    // Confirmation holds the order lock shared with request submission/review, preventing stale writes.
    private Task<bool> PendingPauseBlocksSelectionAsync(Guid orderId, DateOnly date, CancellationToken ct) =>
        db.Set<PackageChangeRequest>().AsNoTracking().AnyAsync(r =>
            r.OrderId == orderId && r.Type == "PAUSE" && r.Status == "PENDING_APPROVAL" &&
            r.PauseStartDate != null && r.PauseStartDate <= date && r.ResumeDate > date, ct);
}
