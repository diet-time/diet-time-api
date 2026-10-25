using System.Data;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DietTime.Persistence;

public sealed class MealSelectionOptions
{
    public const string SectionName = "MealSelections";
    public TimeOnly? CutoffTime { get; set; }
    public int? DaysBeforeDelivery { get; set; }
}

public sealed class UpcomingMealService(
    DietTimeDbContext db, TimeProvider clock, IOptions<MealSelectionOptions> policy,
    IStorageUrlService storage) : IUpcomingMealService
{
    public async Task InitializeAsync(Order order, CancellationToken ct)
    {
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct) : null;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({order.Id.ToString()}, 0))", ct);
        var existingDates = (await db.Set<SubscriptionServiceDay>()
            .Where(d => d.OrderId == order.Id).Select(d => d.ServiceDate).ToListAsync(ct)).ToHashSet();
        var template = await db.MealPlanTemplates.SingleAsync(t => t.Id == order.MealPlanTemplateId, ct);
        var profile = await ProfileAsync(order.CustomerProfileId, ct);
        // Quantities come from the purchased order snapshot, even if an admin
        // later changes the price record's package configuration.
        var typeIds = order.Meals.Select(m => m.MealTypeId).ToArray();
        var typeCodes = await db.MealTypes.Where(t => typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Code, ct);
        var mealCount = order.Meals.Where(m => !MealTypeClassification.IsSnack(typeCodes[m.MealTypeId])).Sum(m => m.Quantity);
        var snackCount = order.Meals.Where(m => MealTypeClassification.IsSnack(typeCodes[m.MealTypeId])).Sum(m => m.Quantity);
        var now = clock.GetUtcNow();
        var sequence = 0;
        foreach (var date in SubscriptionMenuRules.ServiceDates(order.StartDate, order.EndDate,
                     order.DeliveryDays.Select(d => d.DayOfWeek).ToHashSet()))
        {
            sequence++;
            var qatarToday = DateOnly.FromDateTime(now.UtcDateTime.AddHours(3));
            if (date < qatarToday || existingDates.Contains(date)) continue; // Never regenerate or overwrite saved choices.
            var day = new SubscriptionServiceDay
            {
                Id = Guid.NewGuid(), SubscriptionId = order.Id, OrderId = order.Id,
                ServiceDate = date, DayOfWeek = (short)date.DayOfWeek,
                MealPlanTemplateId = template.Id, ServiceSequence = sequence,
                MenuWeekNumber = SubscriptionMenuRules.MenuWeek(sequence, template.CycleWeeks),
                MealCount = mealCount, SnackCount = snackCount,
                SelectionCutoffAt = policy.Value.CutoffTime is { } time &&
                    policy.Value.DaysBeforeDelivery is >= 0
                    ? SubscriptionMenuRules.QatarCutoff(date, policy.Value.DaysBeforeDelivery.Value, time) : null,
                CreatedAt = now, UpdatedAt = now, CreatedBy = order.CreatedBy, UpdatedBy = order.CreatedBy
            };
            var candidates = await EligibleAsync(day, order, profile, ct);
            var chosenIds = new HashSet<Guid>();
            foreach (var (category, count) in new[] { ("meal", mealCount), ("snack", snackCount) })
            {
                var choices = candidates.Where(c => Category(c) == category)
                    .OrderByDescending(c => PreferenceScore(c.MealItem, profile.Preferences))
                    .ThenByDescending(c => c.IsDefault).ThenBy(c => c.Slot.DisplayOrder)
                    .ThenBy(c => c.DisplayOrder).ThenBy(c => c.Id);
                var position = 0;
                foreach (var option in choices)
                {
                    if (position == count) break;
                    if (!chosenIds.Add(option.MealItemId)) continue;
                    day.Selections.Add(new CustomerMealSelection
                    {
                        Id = Guid.NewGuid(), SubscriptionServiceDayId = day.Id,
                        MealTypeId = option.Slot.MealTypeId, MenuItemId = option.MealItemId,
                        MealPlanSlotOptionId = option.Id, Category = category, SelectionNumber = ++position,
                        IsDefaultSelection = true, SelectionSource = "DEFAULT",
                        SelectedAt = now, CreatedAt = now, UpdatedAt = now,
                        CreatedBy = order.CreatedBy, UpdatedBy = order.CreatedBy
                    });
                }
                if (position != count)
                    day.ReviewReason = "Not enough eligible meals are configured for this delivery day. Our team needs to review it.";
            }
            day.SelectionStatus = day.ReviewReason is null ? "DEFAULTED" : "PARTIAL";
            db.Add(day);
        }
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<UpcomingMealDayResponse>?> GetDaysAsync(Guid orderId, Guid userId, CancellationToken ct)
    {
        var order = await OwnedOrderAsync(orderId, userId, ct);
        if (order is null) return null;
        var days = await db.Set<SubscriptionServiceDay>().AsNoTracking().Include(d => d.Selections)
            .Where(d => d.OrderId == orderId).OrderBy(d => d.ServiceDate).ToListAsync(ct);
        return days.Select(d =>
        {
            var reason = LockReason(d, order);
            return new UpcomingMealDayResponse(orderId, d.Id, d.ServiceDate,
                d.ServiceSequence ?? 0, d.MenuWeekNumber ?? 0, Status(d, reason),
                reason is null, d.SelectionCutoffAt, reason);
        }).ToArray();
    }

    public async Task<DailyMealMenuResponse?> GetMenuAsync(Guid orderId, Guid dayId, Guid userId, string language, CancellationToken ct)
    {
        var order = await OwnedOrderAsync(orderId, userId, ct);
        if (order is null) return null;
        var day = await db.Set<SubscriptionServiceDay>().AsNoTracking().Include(d => d.Selections)
            .SingleOrDefaultAsync(d => d.OrderId == orderId && d.Id == dayId, ct);
        return day is null ? null : await MapAsync(day, order, language, ct);
    }

    public async Task<DailyMealMenuResponse> ReplaceAsync(Guid orderId, Guid dayId, Guid selectionId,
        Guid userId, string language, ReplaceAssignedMealRequest request, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var order = await OwnedOrderAsync(orderId, userId, ct) ?? throw Missing();
        if (!await db.Set<SubscriptionServiceDay>().AnyAsync(d => d.Id == dayId && d.OrderId == orderId, ct)) throw Missing();
        // Serialize changes and kitchen finalization on this exact service day.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM public.subscription_service_days WHERE id = {dayId} FOR UPDATE", ct);
        var day = await db.Set<SubscriptionServiceDay>().Include(d => d.Selections).SingleAsync(d => d.Id == dayId, ct);
        var reason = LockReason(day, order);
        if (reason is not null) throw new UpcomingMealException(409, "selection_locked", reason);
        if (request.ExpectedRevision != day.Revision)
            throw new UpcomingMealException(409, "selection_conflict", "Your meals changed. Refresh this day before trying again.");
        var selection = day.Selections.SingleOrDefault(s => s.Id == selectionId && s.Status != "CANCELLED") ?? throw Missing();
        var profile = await ProfileAsync(order.CustomerProfileId, ct);
        var eligible = await EligibleAsync(day, order, profile, ct);
        var replacement = eligible.SingleOrDefault(o => o.Id == request.SlotOptionId && o.MealItemId == request.MealItemId);
        if (replacement is null || Category(replacement) != selection.Category)
            throw new UpcomingMealException(422, "meal_unavailable", "That meal is not available for this selection and delivery day.");
        if (day.Selections.Any(s => s.Id != selectionId && s.Status != "CANCELLED" && s.MenuItemId == replacement.MealItemId))
            throw new UpcomingMealException(422, "duplicate_meal", "This dish is already included in your meals for this day.");
        var now = clock.GetUtcNow();
        db.Add(new CustomerMealSelectionHistory
        {
            Id = Guid.NewGuid(), CustomerMealSelectionId = selection.Id,
            SubscriptionServiceDayId = day.Id, MealTypeId = replacement.Slot.MealTypeId,
            PreviousMenuItemId = selection.MenuItemId, NewMenuItemId = replacement.MealItemId,
            ChangedAt = now, ChangedBy = userId
        });
        selection.MenuItemId = replacement.MealItemId;
        selection.MealPlanSlotOptionId = replacement.Id;
        selection.MealTypeId = replacement.Slot.MealTypeId;
        selection.SelectionSource = "CUSTOMER";
        selection.IsDefaultSelection = false;
        selection.LastChangedAt = selection.UpdatedAt = now;
        selection.UpdatedBy = userId;
        day.SelectionStatus = "COMPLETED";
        day.UpdatedAt = now;
        day.UpdatedBy = userId;
        day.Revision++;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await MapAsync(day, order, language, ct);
    }

    private Task<Order?> OwnedOrderAsync(Guid id, Guid userId, CancellationToken ct) =>
        db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == id && db.CustomerProfiles.Any(p =>
            p.Id == o.CustomerProfileId && p.UserId == userId && p.IsActive), ct);

    private Task<CustomerProfile> ProfileAsync(Guid profileId, CancellationToken ct) =>
        db.CustomerProfiles.AsNoTracking().Include(p => p.Allergens).Include(p => p.Preferences)
            .SingleAsync(p => p.Id == profileId, ct);

    private async Task<List<MealPlanSlotOption>> EligibleAsync(SubscriptionServiceDay day, Order order, CustomerProfile profile, CancellationToken ct)
    {
        var weekday = MenuWeekdayExtensions.FromDate(day.ServiceDate);
        var atDelivery = SubscriptionMenuRules.QatarCutoff(day.ServiceDate, 0, order.DeliveryStartTime);
        var allergenIds = profile.Allergens.Select(a => a.AllergenId).ToArray();
        var options = await db.MealPlanSlotOptions.AsNoTracking()
            .Include(o => o.Slot).ThenInclude(s => s.MealType)
            .Include(o => o.MealItem).ThenInclude(m => m.Translations)
            .Include(o => o.MealItem).ThenInclude(m => m.Nutrition)
            .Where(o => o.Slot.Day.MealPlanTemplateId == day.MealPlanTemplateId &&
                o.Slot.Day.WeekNumber == day.MenuWeekNumber && o.Slot.Day.MenuWeekday == weekday &&
                o.Slot.Day.IsActive && o.Slot.IsActive && o.Slot.MealType.IsActive &&
                o.IsAvailable && o.AdditionalPrice == 0 &&
                (o.AvailableFrom == null || o.AvailableFrom <= atDelivery) &&
                (o.AvailableUntil == null || o.AvailableUntil > atDelivery) &&
                o.MealItem.Status == "ACTIVE" && o.MealItem.IsAvailable &&
                (o.MealItem.AvailableFrom == null || o.MealItem.AvailableFrom <= atDelivery) &&
                (o.MealItem.AvailableUntil == null || o.MealItem.AvailableUntil > atDelivery) &&
                !o.MealItem.Allergens.Any(a => allergenIds.Contains(a.AllergenId)))
            .ToListAsync(ct);
        return options.Where(o => MealPreferenceRules.IsEligible(o.MealItem, profile.Preferences)).ToList();
    }

    private async Task<DailyMealMenuResponse> MapAsync(SubscriptionServiceDay day, Order order, string language, CancellationToken ct)
    {
        var profile = await ProfileAsync(order.CustomerProfileId, ct);
        var alternatives = await EligibleAsync(day, order, profile, ct);
        var saved = day.Selections.Where(s => s.Status != "CANCELLED").OrderBy(s => s.Category).ThenBy(s => s.SelectionNumber).ToArray();
        var ids = saved.Select(s => s.MenuItemId).Concat(alternatives.Select(o => o.MealItemId)).Distinct().ToArray();
        // Saved dishes remain visible even if operations later disables an option.
        var meals = await db.MealItems.AsNoTracking().Include(m => m.Translations).Include(m => m.Nutrition)
            .Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
        var media = await db.MealMedia.AsNoTracking().Where(m => ids.Contains(m.EntityId) &&
            m.Status == "ACTIVE" && m.MediaType == MealMediaTypes.MealItem)
            .OrderByDescending(m => m.IsPrimary).ThenBy(m => m.DisplayOrder).ToListAsync(ct);
        UpcomingMealItemResponse Item(Guid id, Guid optionId, string category, Guid typeId)
        {
            meals.TryGetValue(id, out var meal);
            var text = meal?.Translations.FirstOrDefault(t => t.LanguageCode == language)
                ?? meal?.Translations.FirstOrDefault(t => t.LanguageCode == "en") ?? meal?.Translations.FirstOrDefault();
            var image = media.FirstOrDefault(m => m.EntityId == id);
            var imageUrl = image?.ThumbnailUrl ?? image?.PublicUrl;
            if (string.IsNullOrWhiteSpace(imageUrl) && image is not null)
                imageUrl = storage.GetPublicUrl(image.ThumbnailObjectKey ?? image.ObjectKey);
            return new(id, optionId, text?.Name ?? meal?.Sku ?? "Meal no longer available",
                text?.ShortDescription, imageUrl, meal?.Nutrition?.CaloriesKcal, category, typeId);
        }
        var reason = LockReason(day, order);
        return new(order.Id, day.Id, day.ServiceDate, day.MenuWeekNumber ?? 0,
            day.ServiceDate.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)day.ServiceDate.DayOfWeek,
            day.MealCount ?? 0, day.SnackCount ?? 0, Status(day, reason), reason is null,
            day.SelectionCutoffAt, reason, day.Revision,
            saved.Select(s => new AssignedMealResponse(s.Id,
                s.SelectionSource == "DEFAULT" ? "automatic" : s.SelectionSource.ToLowerInvariant(),
                Item(s.MenuItemId, s.MealPlanSlotOptionId ?? Guid.Empty, s.Category ?? "meal", s.MealTypeId))).ToArray(),
            alternatives.Select(o => Item(o.MealItemId, o.Id, Category(o), o.Slot.MealTypeId)).ToArray());
    }

    private string? LockReason(SubscriptionServiceDay day, Order order)
    {
        if (day.Status == "DELIVERED") return "This delivery has been completed.";
        if (order.Status is not ("CONFIRMED" or "ACTIVE")) return "This subscription is not active.";
        if (day.SelectionLocked || day.SelectionStatus == "LOCKED" || day.Status is not ("PLANNED" or "CONFIRMED"))
            return "Your meals are locked for kitchen preparation.";
        if (day.ReviewReason is not null) return day.ReviewReason;
        if (day.Selections.Count(s => s.Category == "meal" && s.Status != "CANCELLED") != day.MealCount ||
            day.Selections.Count(s => s.Category == "snack" && s.Status != "CANCELLED") != day.SnackCount)
            return "Our team needs to review the meal quantities for this day.";
        if (day.SelectionCutoffAt is null) return "The selection deadline is being confirmed. Your meals are read-only.";
        if (clock.GetUtcNow() >= day.SelectionCutoffAt.Value) return "The selection deadline has passed. Your meals are locked for kitchen preparation.";
        return null;
    }

    private static string Status(SubscriptionServiceDay day, string? reason) => day.Status == "DELIVERED" ? "delivered"
        : day.ReviewReason is not null ? "needs_review" : reason is not null ? "locked"
        : day.SelectionStatus == "COMPLETED" ? "customized" : "auto_selected";
    private static string Category(MealPlanSlotOption option) => MealTypeClassification.IsSnack(option.Slot.MealType.Code) ? "snack" : "meal";
    private static decimal PreferenceScore(MealItem meal, IEnumerable<CustomerProfilePreference> preferences) =>
        MealPreferenceRules.Score(meal, preferences);
    private static UpcomingMealException Missing() => new(404, "service_day_not_found", "This delivery day or meal selection was not found.");
}

public static class MealPreferenceRules
{
    public static bool IsEligible(MealItem meal, IEnumerable<CustomerProfilePreference> preferences) =>
        preferences.All(p => p.PreferenceCode.Trim().ToUpperInvariant() switch
        {
            "VEGAN" => meal.IsVegan,
            "VEGETARIAN" => meal.IsVegetarian || meal.IsVegan,
            "GLUTEN_FREE" => meal.IsGlutenFree,
            "DAIRY_FREE" => meal.IsDairyFree,
            "NUT_FREE" => meal.IsNutFree,
            "NO_SPICY" => !meal.IsSpicy,
            "NONE" or "HIGH_PROTEIN" or "LOW_CARB" or "CHICKEN" or "BEEF" or "SEAFOOD" or
                "ARABIC_CUISINE" or "BREAKFAST_LOVER" or "HEALTHY_SNACKS" => true,
            _ => false // Unknown requirements need review; never infer dietary suitability.
        });

    public static decimal Score(MealItem meal, IEnumerable<CustomerProfilePreference> preferences)
    {
        decimal score = 0;
        foreach (var preference in preferences)
        {
            var code = preference.PreferenceCode.Trim().ToUpperInvariant();
            var weight = Math.Max(1, preference.PreferencePriority);
            if (code == "HIGH_PROTEIN") score += (meal.Nutrition?.ProteinGrams ?? 0) * weight;
            else if (code == "LOW_CARB") score -= (meal.Nutrition?.CarbohydratesGrams ?? 0) * weight;
            else if (code is "CHICKEN" or "BEEF" or "SEAFOOD" or "ARABIC_CUISINE" or "BREAKFAST_LOVER" or "HEALTHY_SNACKS")
            {
                var token = code.Split('_')[0];
                if (meal.Translations.Any(t => t.LanguageCode == "en" && t.Name.Contains(token, StringComparison.OrdinalIgnoreCase)))
                    score += weight;
            }
        }
        return score;
    }
}
