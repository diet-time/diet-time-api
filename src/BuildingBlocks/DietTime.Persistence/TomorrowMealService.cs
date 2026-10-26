using Microsoft.Extensions.Logging;
using System.Data;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Persistence;

public sealed partial class UpcomingMealService
{
    public async Task<TomorrowMenuResponse?> GetTomorrowAsync(Guid orderId, Guid userId, string language, CancellationToken ct)
    {
        var order = await OwnedOrderAsync(orderId, userId, ct);
        if (order is null) return null;
        var tomorrow = SubscriptionMenuRules.QatarToday(clock.GetUtcNow()).AddDays(1);
        var day = tomorrow.DayOfWeek == DayOfWeek.Friday ? null : await db.Set<SubscriptionServiceDay>()
            .AsNoTracking().Include(d => d.Selections)
            .SingleOrDefaultAsync(d => d.OrderId == orderId && d.ServiceDate == tomorrow &&
                d.Status != "SKIPPED" && d.Status != "CANCELLED", ct);
        if (day is null) return new(orderId, null, tomorrow, order.PlanName, 0, 0, "NO_DELIVERY", true, false,
            SubscriptionMenuRules.TomorrowCutoff(tomorrow), clock.GetUtcNow(), null, [], [], [], []);
        return await TomorrowResponseAsync(day, order, language, ct);
    }

    public async Task<TomorrowMenuResponse> ConfirmAsync(Guid orderId, Guid dayId, Guid userId,
        string language, ConfirmMealsRequest request, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var order = await OwnedOrderAsync(orderId, userId, ct) ?? throw Missing();
        var day = await LockDayAsync(dayId, orderId, ct);
        order = await OwnedOrderAsync(orderId, userId, ct) ?? throw Missing();
        // The clock is read after acquiring the lock; waiting cannot extend the window.
        if (!SubscriptionMenuRules.IsSelectionOpen(day.ServiceDate, clock.GetUtcNow()))
            throw new UpcomingMealException(409, "cutoff_passed", "Meal selection is available only for tomorrow before 9:00 PM Qatar time.");
        if (day.SelectionLocked || day.SelectionStatus == "LOCKED" || day.CustomerConfirmedAt != null || day.AutoFinalizedAt != null)
            throw new UpcomingMealException(409, "already_confirmed", "Your meals are already finalized. Refresh to see your saved dishes.");
        if (!Active(order, day))
            throw new UpcomingMealException(409, "subscription_inactive", "This subscription is no longer eligible for meal selection.");
        var meals = request.MealMenuItemIds;
        var snacks = request.SnackMenuItemIds;
        if (meals is null || snacks is null || meals.Count != day.MealCount || snacks.Count != day.SnackCount ||
            meals.Concat(snacks).Distinct().Count() != meals.Count + snacks.Count || meals.Concat(snacks).Contains(Guid.Empty))
            throw new UpcomingMealException(422, "invalid_selection", "Choose the exact number of different meals and snacks in your package.");
        var profile = await ProfileAsync(order.CustomerProfileId, ct);
        var eligible = await EligibleAsync(day, order, profile, ct);
        var chosen = new List<MealPlanSlotOption>();
        foreach (var (ids, category) in new[] { (meals, "meal"), (snacks, "snack") })
        foreach (var id in ids)
        {
            var option = eligible.Where(o => o.MealItemId == id && Category(o) == category)
                .OrderByDescending(o => o.IsDefault).ThenBy(o => o.DisplayOrder).ThenBy(o => o.Id).FirstOrDefault();
            if (option is null)
                throw new UpcomingMealException(422, "meal_unavailable", "A selected dish is no longer available or suitable for your dietary needs. Refresh and choose again.");
            chosen.Add(option);
        }
        // Recheck immediately before writing, including time spent retrieving eligibility.
        if (!SubscriptionMenuRules.IsSelectionOpen(day.ServiceDate, clock.GetUtcNow()))
            throw new UpcomingMealException(409, "cutoff_passed", "The 9:00 PM selection deadline has passed.");
        await SaveSelectionSetAsync(day, chosen, "CUSTOMER", userId, ct);
        day.CustomerConfirmedAt = clock.GetUtcNow();
        day.SelectionStatus = "COMPLETED"; // Existing database check-constraint value.
        day.SelectionLocked = true;
        day.SelectionCutoffAt = SubscriptionMenuRules.TomorrowCutoff(day.ServiceDate);
        day.ReviewReason = null;
        day.UpdatedAt = clock.GetUtcNow();
        day.UpdatedBy = userId;
        day.Revision++;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await TomorrowResponseAsync(day, order, language, ct);
    }

    public async Task FinalizeDueAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        // Includes missed earlier cutoffs after a restart; no dependency on scheduler history.
        var latestDue = SubscriptionMenuRules.QatarToday(now);
        if (now >= SubscriptionMenuRules.TomorrowCutoff(latestDue.AddDays(1))) latestDue = latestDue.AddDays(1);
        var ids = await db.Set<SubscriptionServiceDay>().AsNoTracking()
            .Where(d => d.OrderId != null && d.ServiceDate <= latestDue && !d.SelectionLocked &&
                d.SelectionStatus != "LOCKED" &&
                d.CustomerConfirmedAt == null && d.AutoFinalizedAt == null &&
                (d.Status == "PLANNED" || d.Status == "CONFIRMED"))
            .OrderBy(d => d.ServiceDate).Select(d => new { d.Id, OrderId = d.OrderId!.Value }).ToListAsync(ct);
        foreach (var item in ids)
        {
            try { await FinalizeDayAsync(item.Id, item.OrderId, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                // One broken delivery must not prevent other customers' finalization.
                db.ChangeTracker.Clear();
                logger?.LogError(error, "Meal finalization failed for service day {ServiceDayId}; flagged for review and retry.", item.Id);
                try
                {
                    var failedAt = clock.GetUtcNow();
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE public.subscription_service_days SET review_reason = 'Automatic finalization failed. Operations must review this delivery.', updated_at = {failedAt} WHERE id = {item.Id} AND NOT selection_locked AND customer_confirmed_at IS NULL AND auto_finalized_at IS NULL", ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception flagError) { logger?.LogError(flagError, "Could not flag meal finalization failure for {ServiceDayId}.", item.Id); }
            }
        }
    }

    private async Task FinalizeDayAsync(Guid dayId, Guid orderId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var day = await LockDayAsync(dayId, orderId, ct);
        if (day.SelectionLocked || day.SelectionStatus == "LOCKED" || day.CustomerConfirmedAt != null || day.AutoFinalizedAt != null ||
            clock.GetUtcNow() < SubscriptionMenuRules.TomorrowCutoff(day.ServiceDate)) return;
        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId, ct);
        var chosen = new List<MealPlanSlotOption>();
        var valid = Active(order, day) && day.ServiceDate.DayOfWeek != DayOfWeek.Friday &&
            day.MealCount is >= 1 and <= 5 && day.SnackCount is >= 0;
        if (valid)
        {
            var profile = await ProfileAsync(order.CustomerProfileId, ct);
            valid &= profile.IsActive;
            var eligible = profile.IsActive ? await EligibleAsync(day, order, profile, ct) : [];
            var savedIds = day.Selections.Where(s => s.Status != "CANCELLED").Select(s => s.MenuItemId).ToHashSet();
            var used = new HashSet<Guid>();
            foreach (var (category, count) in new[] { ("meal", day.MealCount!.Value), ("snack", day.SnackCount!.Value) })
            {
                var choices = eligible.Where(o => Category(o) == category)
                    .OrderByDescending(o => savedIds.Contains(o.MealItemId))
                    .ThenByDescending(o => PreferenceScore(o.MealItem, profile.Preferences))
                    .ThenByDescending(o => o.IsDefault).ThenBy(o => o.Slot.DisplayOrder).ThenBy(o => o.DisplayOrder).ThenBy(o => o.Id);
                var assigned = 0;
                foreach (var option in choices)
                {
                    if (assigned == count) break;
                    if (!used.Add(option.MealItemId)) continue;
                    chosen.Add(option);
                    assigned++;
                }
                valid &= assigned == count;
            }
        }
        if (!valid)
        {
            const string reason = "Automatic finalization needs review: subscription or eligible meal quantities are incomplete. Do not prepare provisional dishes.";
            if (day.ReviewReason != reason) { day.ReviewReason = reason; day.UpdatedAt = clock.GetUtcNow(); day.Revision++; }
            // Keep unlocked so operations can fix configuration and the next pass can retry.
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return;
        }
        await SaveSelectionSetAsync(day, chosen, "DEFAULT", null, ct);
        day.AutoFinalizedAt = clock.GetUtcNow();
        day.SelectionStatus = "DEFAULTED";
        day.SelectionLocked = true;
        day.SelectionCutoffAt = SubscriptionMenuRules.TomorrowCutoff(day.ServiceDate);
        day.ReviewReason = null;
        day.UpdatedAt = clock.GetUtcNow();
        day.Revision++;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task<SubscriptionServiceDay> LockDayAsync(Guid dayId, Guid orderId, CancellationToken ct)
    {
        if (!await db.Set<SubscriptionServiceDay>().AnyAsync(d => d.Id == dayId && d.OrderId == orderId, ct)) throw Missing();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM public.subscription_service_days WHERE id = {dayId} FOR UPDATE", ct);
        var day = await db.Set<SubscriptionServiceDay>().Include(d => d.Selections).SingleAsync(d => d.Id == dayId, ct);
        // Also works when a scoped context already loaded this day before waiting.
        await db.Entry(day).ReloadAsync(ct);
        foreach (var selection in day.Selections.ToArray()) await db.Entry(selection).ReloadAsync(ct);
        await db.Entry(day).Collection(d => d.Selections).Query().LoadAsync(ct);
        return day;
    }

    private async Task SaveSelectionSetAsync(SubscriptionServiceDay day, List<MealPlanSlotOption> choices,
        string source, Guid? actor, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        // Free the non-cancelled dish index first. Positions remain occupied and are reused,
        // including cancelled positions. Both saves are inside the caller's transaction.
        foreach (var row in day.Selections) { row.Status = "CANCELLED"; row.UpdatedAt = now; }
        await db.SaveChangesAsync(ct);
        foreach (var category in new[] { "meal", "snack" })
        {
            var position = 0;
            foreach (var option in choices.Where(o => Category(o) == category))
            {
                position++;
                var row = day.Selections.SingleOrDefault(s => s.Category == category && s.SelectionNumber == position);
                if (row is null)
                {
                    row = new CustomerMealSelection { Id = Guid.NewGuid(), SubscriptionServiceDayId = day.Id,
                        Category = category, SelectionNumber = position, CreatedAt = now, CreatedBy = actor };
                    day.Selections.Add(row);
                }
                else
                {
                    db.Add(new CustomerMealSelectionHistory { Id = Guid.NewGuid(), CustomerMealSelectionId = row.Id,
                        SubscriptionServiceDayId = day.Id, MealTypeId = option.Slot.MealTypeId,
                        PreviousMenuItemId = row.MenuItemId, NewMenuItemId = option.MealItemId,
                        ChangeSource = source, ChangedAt = now, ChangedBy = actor });
                }
                row.MenuItemId = option.MealItemId;
                row.MealPlanSlotOptionId = option.Id;
                row.MealTypeId = option.Slot.MealTypeId;
                row.Status = "SELECTED";
                row.SelectionSource = source;
                row.IsDefaultSelection = source == "DEFAULT";
                row.SelectedAt = row.UpdatedAt = now;
                row.LastChangedAt = now;
                row.UpdatedBy = actor;
            }
        }
    }

    private static bool Active(Order order, SubscriptionServiceDay day) =>
        order.Status is "CONFIRMED" or "ACTIVE" && day.Status is "PLANNED" or "CONFIRMED" &&
        order.StartDate <= day.ServiceDate && order.EndDate >= day.ServiceDate;

    private async Task<TomorrowMenuResponse> TomorrowResponseAsync(SubscriptionServiceDay day, Order order, string language, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var locked = day.SelectionLocked || day.SelectionStatus == "LOCKED" || day.CustomerConfirmedAt != null || day.AutoFinalizedAt != null;
        var canConfirm = !locked && Active(order, day) && day.MealCount is >= 1 and <= 5 && day.SnackCount is >= 0 &&
            SubscriptionMenuRules.IsSelectionOpen(day.ServiceDate, now);
        // Incomplete provisional defaults must not stop a customer completing a valid set.
        var menu = await MapAsync(day, order, language, ct, includeEligible: canConfirm);
        var items = canConfirm ? menu.AvailableAlternatives : [];
        var status = day.CustomerConfirmedAt != null ? "CUSTOMER_CONFIRMED" : day.AutoFinalizedAt != null ? "AUTO_FINALIZED"
            : locked ? "LOCKED" : day.ReviewReason != null ? "NEEDS_REVIEW"
            : now >= SubscriptionMenuRules.TomorrowCutoff(day.ServiceDate) ? "FINALIZATION_PENDING" : "DEFAULT_PENDING";
        return new(order.Id, day.Id, day.ServiceDate, order.PlanName, day.MealCount ?? 0, day.SnackCount ?? 0,
            status, locked, canConfirm, SubscriptionMenuRules.TomorrowCutoff(day.ServiceDate), now, day.ReviewReason,
            menu.SelectedMeals.Select(s => s.Meal.MealItemId).ToArray(), menu.SelectedMeals.Select(s => s.Meal).ToArray(),
            items.Where(i => i.Category == "meal").DistinctBy(i => i.MealItemId).ToArray(),
            items.Where(i => i.Category == "snack").DistinctBy(i => i.MealItemId).ToArray());
    }
}
