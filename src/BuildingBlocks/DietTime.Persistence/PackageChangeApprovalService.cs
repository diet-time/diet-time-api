using System.Text.Json;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Persistence;

public sealed partial class UpcomingMealService
{
    private sealed record ResumeApprovalPayload(ResumePackageRequest Request, string ScheduleVersion);

    private static PackageChangeResponse ChangeResponse(PackageChangeRequest r) => new(r.Id, r.Type, r.Status,
        r.PauseStartDate, r.ResumeDate, r.ProjectedEndDate, r.Reason, r.CreatedAt, r.ReviewedAt, r.ReviewReason);

    public async Task<PackageManagementResponse> RequestPauseAsync(Guid orderId, Guid userId, PausePackageRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await LockPackageAsync(orderId, userId, ct);
        if (await ReplayChangeAsync(orderId, userId, request.CommandId, "PAUSE", JsonSerializer.Serialize(request), ct)) {
            await tx.CommitAsync(ct); return await GetPackageAsync(orderId, userId, ct);
        }
        if (request.Reason?.Length > 500) throw new UpcomingMealException(400, "invalid_pause_request", "Keep the reason within 500 characters.");
        if (order.RowVersion != request.ExpectedRowVersion) throw PauseError("order_changed", "The package changed. Refresh the preview before requesting a pause.");
        var preview = await PreviewPauseAsync(orderId, userId, new(request.PauseStartDate, request.ResumeDate, request.MoveRemaining), ct);
        if (preview.ScheduleVersion != request.ScheduleVersion) throw PauseError("schedule_changed", "The schedule changed. Preview again before submitting.");
        db.Add(new PackageChangeRequest { Id = request.CommandId, OrderId = orderId, Type = "PAUSE", RequestedBy = userId,
            CreatedAt = clock.GetUtcNow(), PauseStartDate = preview.PauseStartDate, ResumeDate = request.ResumeDate,
            ProjectedEndDate = preview.ProjectedEndDate, Reason = request.Reason?.Trim(), PayloadJson = JsonSerializer.Serialize(request) });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return await GetPackageAsync(orderId, userId, ct);
    }

    public async Task<PackageManagementResponse> RequestResumeAsync(Guid orderId, Guid userId, ResumePackageRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await LockPackageAsync(orderId, userId, ct);
        if (await ReplayChangeAsync(orderId, userId, request.CommandId, "RESUME", JsonSerializer.Serialize(request), ct)) {
            await tx.CommitAsync(ct); return await GetPackageAsync(orderId, userId, ct);
        }
        if (order.RowVersion != request.ExpectedRowVersion) throw PauseError("order_changed", "The package changed. Refresh before requesting a restart.");
        var pause = await db.Set<SubscriptionPause>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == request.PauseId && p.OrderId == orderId, ct);
        var now = clock.GetUtcNow();
        if (!OrderWorkflowPolicy.CanFulfill(order) || pause == null || pause.Status != "CONFIRMED" || pause.ResumeDate <= SubscriptionMenuRules.QatarToday(now))
            throw PauseError("resume_not_available", "This package cannot be resumed early.");
        var days = await PackageDaysAsync(orderId, ct);
        var pausedIds = JsonSerializer.Deserialize<ScheduleChange[]>(pause.ScheduleChangesJson)!.Select(c => c.ServiceDayId).ToHashSet();
        var moving = days.Where(d => pausedIds.Contains(d.Id) && PackagePauseRules.CanMove(d, now)).ToArray();
        if (moving.Length == 0) throw PauseError("no_eligible_service_days", "No unlocked future deliveries are available to restart.");
        var first = request.ResumeDate ?? new[] { PackagePauseRules.MinimumResumeDate(now), pause.PauseStartDate }.Max();
        PackagePauseRules.ValidateResumeDate(first, now);
        var schedule = PackagePauseRules.Schedule(moving, days, first, order.DeliveryDays.Select(d => d.DayOfWeek).ToHashSet());
        if (request.ResumeDate == null && schedule.Values.Min() >= pause.ResumeDate)
            throw PauseError("resume_not_available", "No earlier eligible restart is available.");
        db.Add(new PackageChangeRequest { Id = request.CommandId, OrderId = orderId, Type = "RESUME", RequestedBy = userId,
            CreatedAt = now, ResumeDate = first, ProjectedEndDate = days.Where(HasEntitlement).Max(d => schedule.GetValueOrDefault(d.Id, d.ServiceDate)),
            PayloadJson = JsonSerializer.Serialize(new ResumeApprovalPayload(request, ScheduleVersion(days, now))) });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return await GetPackageAsync(orderId, userId, ct);
    }

    private async Task<bool> ReplayChangeAsync(Guid orderId, Guid userId, Guid id, string type, string payload, CancellationToken ct)
    {
        if (id == Guid.Empty) throw new UpcomingMealException(400, "invalid_package_request", "A command ID is required.");
        var existing = await db.Set<PackageChangeRequest>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        if (existing != null) {
            // jsonb may normalize whitespace/property order; compare deserialized content, not its storage representation.
            var same = type == "PAUSE" ? JsonSerializer.Deserialize<PausePackageRequest>(existing.PayloadJson) == JsonSerializer.Deserialize<PausePackageRequest>(payload)
                : JsonSerializer.Deserialize<ResumeApprovalPayload>(existing.PayloadJson)!.Request == JsonSerializer.Deserialize<ResumePackageRequest>(payload);
            if (existing.OrderId != orderId || existing.RequestedBy != userId || existing.Type != type || !same)
                throw PauseError("idempotency_conflict", "This command ID belongs to a different request.");
            return true;
        }
        if (await db.Set<PackageChangeRequest>().AnyAsync(r => r.OrderId == orderId && r.Status == "PENDING_APPROVAL", ct))
            throw PauseError("package_request_pending", "A package change is already awaiting staff confirmation.");
        return false;
    }

    public async Task<StaffPackageChangeListResponse> ListChangeRequestsAsync(int page, int size, CancellationToken ct)
    {
        var query = db.Set<PackageChangeRequest>().AsNoTracking().Where(r => r.Status == "PENDING_APPROVAL");
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).Skip((page - 1) * size).Take(size)
            .Select(r => new { Request = r, r.Order.OrderNumber, r.Order.PlanName, r.Order.CustomerProfileId }).ToArrayAsync(ct);
        var profileIds = rows.Select(r => r.CustomerProfileId).Distinct().ToArray();
        var profiles = await db.CustomerProfiles.AsNoTracking().Where(p => profileIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        return new(rows.Select(r => new StaffPackageChangeResponse(r.Request.OrderId, r.OrderNumber, r.PlanName,
            profiles.GetValueOrDefault(r.CustomerProfileId)?.PreferredName ?? "Customer", ChangeResponse(r.Request))).ToArray(), total, page, size);
    }

    public async Task<PackageChangeResponse> ReviewChangeRequestAsync(Guid requestId, Guid reviewer, PackageChangeReviewRequest request, CancellationToken ct)
    {
        if (request.Reason?.Length > 500 || !request.Approve && string.IsNullOrWhiteSpace(request.Reason))
            throw new UpcomingMealException(400, "review_reason_required", "Provide a rejection reason within 500 characters.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var orderId = await db.Set<PackageChangeRequest>().Where(r => r.Id == requestId).Select(r => (Guid?)r.OrderId).SingleOrDefaultAsync(ct) ?? throw Missing();
        // Match the order-first lock order used by confirmation, fulfillment and pause/resume.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM public.orders WHERE id = {orderId} FOR UPDATE", ct);
        var change = await db.Set<PackageChangeRequest>().SingleAsync(r => r.Id == requestId, ct);
        await db.Entry(change).ReloadAsync(ct);
        var target = request.Approve ? "APPROVED" : "REJECTED";
        if (change.Status != "PENDING_APPROVAL") {
            if (change.Status != target) throw PauseError("request_already_reviewed", "This request has already been reviewed.");
            await tx.CommitAsync(ct); return ChangeResponse(change);
        }
        if (request.Approve) {
            // These engines revalidate ownership, order/schedule versions, cutoff and committed days.
            // Both the schedule mutation and decision commit atomically; stale approvals remain pending.
            if (change.Type == "PAUSE")
                await PauseAsync(orderId, change.RequestedBy, JsonSerializer.Deserialize<PausePackageRequest>(change.PayloadJson)!, ct);
            else {
                var snapshot = JsonSerializer.Deserialize<ResumeApprovalPayload>(change.PayloadJson)!;
                var days = await LockedPackageDaysAsync(orderId, ct);
                if (snapshot.ScheduleVersion != ScheduleVersion(days, clock.GetUtcNow()))
                    throw PauseError("schedule_changed", "Delivery preparation changed. Decline this request and ask the customer to submit current dates.");
                await ResumeAsync(orderId, change.RequestedBy, snapshot.Request with { ResumeDate = change.ResumeDate }, ct);
            }
        }
        change.Status = target; change.ReviewedBy = reviewer; change.ReviewedAt = clock.GetUtcNow(); change.ReviewReason = request.Reason?.Trim();
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return ChangeResponse(change);
    }
}
