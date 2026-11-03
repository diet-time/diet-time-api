using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Persistence;

// Reuses eligibility, menu rotation and selection auditing from UpcomingMealService.
public sealed partial class UpcomingMealService : IPackageManagementService
{
    private sealed record ScheduleChange(Guid ServiceDayId, DateOnly From, DateOnly To, DateTimeOffset? FromCutoff = null, DateOnly? RequestedResumeDate = null);
    private static UpcomingMealException PauseError(string code, string message) => new(409, code, message);
    private static string ScheduleVersion(IEnumerable<SubscriptionServiceDay> days, DateTimeOffset now) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join(";", days.OrderBy(d => d.Id).Select(d => $"{d.Id}|{d.ServiceDate:yyyy-MM-dd}|{d.Revision}|{d.Status}|{d.SelectionStatus}|{d.SelectionLocked}|{d.CustomerConfirmedAt:O}|{d.AutoFinalizedAt:O}|{d.FulfilledAt:O}|{d.SelectionCutoffAt:O}|{PackagePauseRules.CanMove(d, now)}")))));
    private static bool HasEntitlement(SubscriptionServiceDay d) => d.Status is not ("SKIPPED" or "CANCELLED");
    private static bool IsWeekday(Order o, SubscriptionServiceDay d) => o.DeliveryDays.Any(w => w.DayOfWeek == (d.ServiceDate.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.ServiceDate.DayOfWeek));
    private static string PauseStatus(SubscriptionPause p, DateOnly today) => p.Status == "RESUMED" ? "RESUMED" : today < p.PauseStartDate ? "SCHEDULED" : today < p.ResumeDate ? "PAUSED" : "COMPLETED";
    private static PackagePauseResponse PauseResponse(SubscriptionPause p, DateOnly today) => new(p.Id, p.PauseStartDate, p.ResumeDate, PauseStatus(p, today), p.Reason);
    private Task<List<SubscriptionServiceDay>> PackageDaysAsync(Guid id, CancellationToken ct) => db.Set<SubscriptionServiceDay>().AsNoTracking().Where(d => d.OrderId == id).OrderBy(d => d.ServiceDate).ToListAsync(ct);

    public async Task<IReadOnlyList<PackagePauseResponse>> GetPausesAsync(Guid orderId, Guid userId, CancellationToken ct)
    {
        _ = await OwnedOrderAsync(orderId, userId, ct) ?? throw Missing();
        var rows = await db.Set<SubscriptionPause>().AsNoTracking().Where(p => p.OrderId == orderId).OrderByDescending(p => p.CreatedAt).ToArrayAsync(ct);
        return rows.Select(p => PauseResponse(p, SubscriptionMenuRules.QatarToday(clock.GetUtcNow()))).ToArray();
    }

    public async Task<PackageManagementResponse> GetPackageAsync(Guid orderId, Guid userId, CancellationToken ct)
    {
        var order = await OwnedOrderAsync(orderId, userId, ct) ?? throw Missing();
        var days = await PackageDaysAsync(orderId, ct);
        var now = clock.GetUtcNow(); var today = SubscriptionMenuRules.QatarToday(now);
        var pause = await db.Set<SubscriptionPause>().AsNoTracking().Where(p => p.OrderId == orderId && p.Status == "CONFIRMED" && p.ResumeDate > today)
            .OrderByDescending(p => p.CreatedAt).FirstOrDefaultAsync(ct);
        var eligible = OrderWorkflowPolicy.CanFulfill(order) && pause == null
            ? days.Where(d => PackagePauseRules.CanMove(d, now) && IsWeekday(order, d)).Select(d => d.ServiceDate).Distinct().Order().ToArray() : [];
        var remaining = days.Where(HasEntitlement).Where(d => d.Status is not ("DELIVERED" or "COLLECTED")).ToArray();
        var canResume = false;
        if (pause != null && OrderWorkflowPolicy.CanFulfill(order)) {
            var ids = JsonSerializer.Deserialize<ScheduleChange[]>(pause.ScheduleChangesJson)!.Select(c => c.ServiceDayId).ToHashSet();
            var moving = days.Where(d => ids.Contains(d.Id) && PackagePauseRules.CanMove(d, now)).ToArray();
            if (moving.Length > 0) {
                canResume = true;
            }
        }
        return new(orderId, order.RowVersion, now, pause == null ? order.Status : PauseStatus(pause, today), remaining.Length,
            remaining.Where(d => d.ServiceDate >= today).Select(d => (DateOnly?)d.ServiceDate).Min(), order.EndDate,
            eligible, eligible.Length > 0 ? null : pause != null ? "This package already has a scheduled pause." :
                !OrderWorkflowPolicy.CanFulfill(order) ? "This package is not eligible for pausing." : "There are no unlocked future service days available to pause.",
            pause == null ? null : PauseResponse(pause, today), canResume);
    }

    private static (SubscriptionServiceDay[] Moving, IReadOnlyDictionary<Guid, DateOnly> Schedule, PausePreviewResponse Preview) BuildPause(
        Order order, List<SubscriptionServiceDay> days, PausePreviewRequest request, DateTimeOffset now)
    {
        if (!OrderWorkflowPolicy.CanFulfill(order)) throw PauseError("package_not_active", "This package is not eligible for pausing.");
        var eligible = days.Where(d => PackagePauseRules.CanMove(d, now) && IsWeekday(order, d)).OrderBy(d => d.ServiceDate).ToArray();
        if (eligible.Length == 0) throw PauseError("no_eligible_service_days", "There are no unlocked future service days available to pause.");
        if (request.MoveRemaining ? request.PauseStartDate != PackagePauseRules.MinimumResumeDate(now) && request.PauseStartDate != eligible[0].ServiceDate : !eligible.Any(d => d.ServiceDate == request.PauseStartDate))
            throw PauseError("pause_date_unavailable", "Choose an unlocked future service date. Today's delivery and committed deliveries cannot be paused.");
        if (request.MoveRemaining ? request.ResumeDate < request.PauseStartDate : request.ResumeDate <= request.PauseStartDate)
            throw new UpcomingMealException(400, "invalid_resume_date", "Choose a resume date after the pause start.");
        PackagePauseRules.ValidateResumeDate(request.ResumeDate, now);
        if (days.Select(d => d.SubscriptionId).Distinct().Count() != 1)
            throw PauseError("schedule_review_required", "This order contains multiple subscriptions and needs a schedule review.");
        var effectiveStart = request.MoveRemaining ? eligible[0].ServiceDate : request.PauseStartDate;
        var moving = eligible.Where(d => d.ServiceDate >= effectiveStart).ToArray();
        if (moving.Any(d => d.ServiceSequence is not > 0 || d.MealPlanTemplateId == null || d.MealCount is null or < 1 || d.SnackCount is null or < 0))
            throw PauseError("schedule_review_required", "The saved menu schedule needs review by Diet Time before it can be moved.");
        var schedule = PackagePauseRules.Schedule(moving, days, request.ResumeDate, order.DeliveryDays.Select(d => d.DayOfWeek).ToHashSet());
        var end = days.Where(HasEntitlement).Select(d => schedule.GetValueOrDefault(d.Id, d.ServiceDate)).Max();
        return (moving, schedule, new(order.RowVersion, ScheduleVersion(days, now), eligible[0].ServiceDate,
            effectiveStart, request.ResumeDate, moving.Count(d => d.ServiceDate < request.ResumeDate), moving.Length,
            days.Count(d => HasEntitlement(d) && d.Status is not ("DELIVERED" or "COLLECTED") && d.ServiceDate >= SubscriptionMenuRules.QatarToday(now) && d.ServiceDate < request.ResumeDate && !PackagePauseRules.CanMove(d, now)), end) {
                RemainingServiceDays = days.Count(d => HasEntitlement(d) && d.Status is not ("DELIVERED" or "COLLECTED")),
                ActualRestartDate = schedule.Values.Min()
            });
    }

    public async Task<PausePreviewResponse> PreviewPauseAsync(Guid orderId, Guid userId, PausePreviewRequest request, CancellationToken ct)
    {
        var order = await OwnedOrderAsync(orderId, userId, ct) ?? throw Missing();
        if (await db.Set<SubscriptionPause>().AnyAsync(p => p.OrderId == orderId && p.Status == "CONFIRMED" && p.ResumeDate > SubscriptionMenuRules.QatarToday(clock.GetUtcNow()), ct))
            throw PauseError("pause_already_scheduled", "This package already has a scheduled pause.");
        return BuildPause(order, await PackageDaysAsync(orderId, ct), request, clock.GetUtcNow()).Preview;
    }

    private async Task<Order> LockPackageAsync(Guid id, Guid userId, CancellationToken ct)
    {
        // All meal confirmation, finalization and fulfillment commands lock the order before its service days.
        if (await OwnedOrderAsync(id, userId, ct) == null) throw Missing();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM public.orders WHERE id = {id} FOR UPDATE", ct);
        var order = await db.Orders.Include(o => o.Meals).Include(o => o.DeliveryDays).SingleAsync(o => o.Id == id, ct);
        await db.Entry(order).ReloadAsync(ct);
        return order;
    }

    private async Task<List<SubscriptionServiceDay>> LockedPackageDaysAsync(Guid id, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM public.subscription_service_days WHERE order_id = {id} ORDER BY id FOR UPDATE", ct);
        var days = await db.Set<SubscriptionServiceDay>().Include(d => d.Selections).Where(d => d.OrderId == id).OrderBy(d => d.ServiceDate).ToListAsync(ct);
        foreach (var day in days) { await db.Entry(day).ReloadAsync(ct); foreach (var row in day.Selections) await db.Entry(row).ReloadAsync(ct); }
        return days;
    }

    public async Task<PackageManagementResponse> PauseAsync(Guid orderId, Guid userId, PausePackageRequest request, CancellationToken ct)
    {
        if (request.CommandId == Guid.Empty || request.Reason?.Length > 500) throw new UpcomingMealException(400, "invalid_pause_request", "Provide a command ID and keep any reason within 500 characters.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await LockPackageAsync(orderId, userId, ct);
        var replay = await db.Set<SubscriptionPause>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == request.CommandId, ct);
        if (replay != null) {
            if (replay.OrderId != orderId || replay.CreatedBy != userId || replay.PauseStartDate != request.PauseStartDate || replay.RequestedResumeDate != request.ResumeDate || replay.Reason != request.Reason?.Trim())
                throw PauseError("idempotency_conflict", "This command ID belongs to a different pause request.");
            await tx.CommitAsync(ct); return await GetPackageAsync(orderId, userId, ct);
        }
        var history = await db.Set<SubscriptionPause>().Where(p => p.OrderId == orderId).ToListAsync(ct);
        if (history.Any(p => p.Status == "CONFIRMED" && p.ResumeDate > SubscriptionMenuRules.QatarToday(clock.GetUtcNow())))
            throw PauseError("pause_already_scheduled", "This package already has a scheduled pause.");
        if (order.RowVersion != request.ExpectedRowVersion) throw PauseError("order_changed", "This package changed. Refresh it and preview again.");
        var days = await LockedPackageDaysAsync(orderId, ct);
        if (history.Count == 0 && days.Count != SubscriptionMenuRules.ServiceDates(order.StartDate, order.EndDate, order.DeliveryDays.Select(d => d.DayOfWeek).ToHashSet()).Count())
            throw PauseError("schedule_review_required", "Some purchased service days are missing. Diet Time must review the schedule before pausing.");
        var plan = BuildPause(order, days, new(request.PauseStartDate, request.ResumeDate, request.MoveRemaining), clock.GetUtcNow());
        if (plan.Preview.ScheduleVersion != request.ScheduleVersion) throw PauseError("schedule_changed", "Delivery preparation changed. Refresh the pause preview.");
        var changes = plan.Moving.Select(d => new ScheduleChange(d.Id, d.ServiceDate, plan.Schedule[d.Id], d.SelectionCutoffAt)).ToArray();
        await MovePackageDaysAsync(order, days, plan.Moving, plan.Schedule, userId, ct);
        db.Add(new SubscriptionPause { Id = request.CommandId, OrderId = orderId, SubscriptionId = plan.Moving[0].SubscriptionId,
            PauseStartDate = request.PauseStartDate, ResumeDate = request.ResumeDate, RequestedResumeDate = request.ResumeDate, Reason = request.Reason?.Trim(),
            ScheduleChangesJson = JsonSerializer.Serialize(changes), CreatedBy = userId, CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() });
        // Network/lock waits may cross midnight or the 21:00 cutoff. Validate the ORIGINAL dates again.
        var now = clock.GetUtcNow();
        if (changes.Any(c => c.From <= SubscriptionMenuRules.QatarToday(now) || now >= SubscriptionMenuRules.TomorrowCutoff(c.From) || c.FromCutoff.HasValue && now >= c.FromCutoff.Value))
            throw PauseError("pause_date_unavailable", "The delivery deadline passed. Preview a later pause date.");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return await GetPackageAsync(orderId, userId, ct);
    }

    public async Task<PackageManagementResponse> ResumeAsync(Guid orderId, Guid userId, ResumePackageRequest request, CancellationToken ct)
    {
        if (request.CommandId == Guid.Empty) throw new UpcomingMealException(400, "invalid_resume_request", "A command ID is required.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await LockPackageAsync(orderId, userId, ct);
        var pause = await db.Set<SubscriptionPause>().SingleOrDefaultAsync(p => p.Id == request.PauseId && p.OrderId == orderId, ct) ?? throw Missing();
        await db.Entry(pause).ReloadAsync(ct);
        if (pause.Status == "RESUMED" && pause.ResumeCommandId == request.CommandId) {
            var previous = JsonSerializer.Deserialize<ScheduleChange[]>(pause.ScheduleChangesJson)!;
            if (previous.LastOrDefault()?.RequestedResumeDate != request.ResumeDate)
                throw PauseError("idempotency_conflict", "This command ID belongs to a different restart date.");
            await tx.CommitAsync(ct); return await GetPackageAsync(orderId, userId, ct);
        }
        if (!OrderWorkflowPolicy.CanFulfill(order) || pause.Status != "CONFIRMED" || pause.ResumeDate <= SubscriptionMenuRules.QatarToday(clock.GetUtcNow()))
            throw PauseError("resume_not_available", "This package cannot be resumed early.");
        if (order.RowVersion != request.ExpectedRowVersion) throw PauseError("order_changed", "This package changed. Refresh before resuming.");
        var days = await LockedPackageDaysAsync(orderId, ct);
        var oldChanges = JsonSerializer.Deserialize<ScheduleChange[]>(pause.ScheduleChangesJson)!;
        var ids = oldChanges.Select(c => c.ServiceDayId).ToHashSet();
        var now = clock.GetUtcNow();
        var moving = days.Where(d => ids.Contains(d.Id) && PackagePauseRules.CanMove(d, now)).ToArray();
        if (moving.Length == 0) throw PauseError("no_eligible_service_days", "There are no unlocked future service days to resume early.");
        var first = request.ResumeDate ?? new[] { PackagePauseRules.MinimumResumeDate(now), pause.PauseStartDate }.Max();
        PackagePauseRules.ValidateResumeDate(first, now);
        var schedule = PackagePauseRules.Schedule(moving, days, first, order.DeliveryDays.Select(d => d.DayOfWeek).ToHashSet());
        if (request.ResumeDate == null && schedule.Values.Min() >= pause.ResumeDate) throw PauseError("resume_not_available", "No earlier eligible delivery date is available.");
        var changes = moving.Select(d => new ScheduleChange(d.Id, d.ServiceDate, schedule[d.Id], d.SelectionCutoffAt, request.ResumeDate)).ToArray();
        await MovePackageDaysAsync(order, days, moving, schedule, userId, ct);
        if (schedule.Values.Any(date => date < PackagePauseRules.MinimumResumeDate(clock.GetUtcNow())) ||
            changes.Any(c => c.FromCutoff.HasValue && clock.GetUtcNow() >= c.FromCutoff.Value))
            throw PauseError("schedule_changed", "The delivery deadline passed. Refresh before resuming.");
        pause.ResumeDate = schedule.Values.Min(); pause.Status = "RESUMED"; pause.ResumeCommandId = request.CommandId; pause.UpdatedAt = clock.GetUtcNow();
        pause.ScheduleChangesJson = JsonSerializer.Serialize(oldChanges.Concat(changes));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return await GetPackageAsync(orderId, userId, ct);
    }

    private async Task MovePackageDaysAsync(Order order, List<SubscriptionServiceDay> all, IReadOnlyList<SubscriptionServiceDay> moving,
        IReadOnlyDictionary<Guid, DateOnly> schedule, Guid actor, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (moving.Any(d => d.ServiceSequence is not > 0 || d.MealPlanTemplateId == null || d.MealCount is null or < 1 || d.SnackCount is null or < 0))
            throw PauseError("schedule_review_required", "The saved menu schedule needs review before it can be moved.");
        if (moving.Any(d => !PackagePauseRules.CanMove(d, now))) throw PauseError("schedule_changed", "A delivery is now committed. Preview again.");
        // Park only unlocked rows inside this transaction to avoid immediate unique-date collisions,
        // in either direction. No observer can see the temporary dates; no records are deleted.
        var parking = all.Select(d => d.ServiceDate).Concat(schedule.Values).Max().AddDays(moving.Count + 31);
        foreach (var day in moving)
        {
            // ck_managed_service_day also applies to intermediate writes inside a transaction.
            // Preserve its Friday exclusion while freeing the unique service dates.
            while (parking.DayOfWeek == DayOfWeek.Friday) parking = parking.AddDays(1);
            day.ServiceDate = parking;
            day.DayOfWeek = (short)parking.DayOfWeek;
            parking = parking.AddDays(1);
        }
        await db.SaveChangesAsync(ct);
        foreach (var day in moving) {
            day.ServiceDate = schedule[day.Id]; day.DayOfWeek = (short)day.ServiceDate.DayOfWeek;
            day.SelectionCutoffAt = SubscriptionMenuRules.TomorrowCutoff(day.ServiceDate);
            var cycle = await db.MealPlanTemplates.Where(t => t.Id == day.MealPlanTemplateId).Select(t => t.CycleWeeks).SingleAsync(ct);
            day.MenuWeekNumber = SubscriptionMenuRules.MenuWeek(day.ServiceSequence!.Value, cycle);
            day.Revision++; day.UpdatedAt = now; day.UpdatedBy = actor;
        }
        order.EndDate = all.Where(HasEntitlement).Max(d => d.ServiceDate);
        // Removing a scheduled pause may restart before the original package start.
        order.StartDate = new[] { order.StartDate, all.Where(HasEntitlement).Min(d => d.ServiceDate) }.Min();
        order.RowVersion++; order.UpdatedAt = now; order.UpdatedBy = actor;
        await db.SaveChangesAsync(ct);
        var profile = await ProfileAsync(order.CustomerProfileId, ct);
        foreach (var day in moving) {
            var options = await EligibleAsync(day, order, ct);
            var saved = day.Selections.Where(s => s.Status != "CANCELLED").ToArray();
            var valid = saved.Count(s => s.Category == "meal") == day.MealCount && saved.Count(s => s.Category == "snack") == day.SnackCount &&
                saved.Where(s => s.Category == "meal").Select(s => s.MealTypeId).Distinct().Count() == day.MealCount &&
                saved.All(s => options.Any(o => o.Id == s.MealPlanSlotOptionId && o.MealItemId == s.MenuItemId && o.Slot.MealTypeId == s.MealTypeId && Category(o) == s.Category));
            if (valid) continue;
            if (saved.Any(s => s.SelectionSource != "DEFAULT")) {
                day.ReviewReason = "Rescheduled customer meal selections need review against the new service date. Saved choices were preserved.";
                continue;
            }
            var assignment = AutomaticMealAssignmentRules.Select(options, day.MealCount ?? 0, day.SnackCount ?? 0,
                o => PreferenceScore(o.MealItem, profile.Preferences), saved.Select(s => s.MenuItemId).ToHashSet());
            await SaveSelectionSetAsync(day, assignment.Meals.Concat(assignment.Snacks).ToList(), "DEFAULT", actor, ct);
            day.ReviewReason = assignment.ReviewReason;
            day.SelectionStatus = assignment.ReviewReason == null ? "DEFAULTED" : day.Selections.Any(s => s.Status != "CANCELLED") ? "PARTIAL" : "PENDING";
        }
    }
}
