using System.Data;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace DietTime.Persistence;

public sealed class MealSelectionOptions
{
    public const string SectionName = "MealSelections";
    public TimeOnly? CutoffTime { get; set; }
    public int? DaysBeforeDelivery { get; set; }
}

public sealed partial class UpcomingMealService(
    DietTimeDbContext db, TimeProvider clock, IOptions<MealSelectionOptions> policy,
    IStorageUrlService storage, ILogger<UpcomingMealService>? logger = null) : IUpcomingMealService
{
    public async Task InitializeAsync(Order order, CancellationToken ct)
    {
        _ = policy; // Retained constructor contract; the new business cutoff is fixed at 21:00 Qatar.
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct) : null;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({order.Id.ToString()}, 0))", ct);
        var existingDays = await db.Set<SubscriptionServiceDay>().AsNoTracking()
            .Where(d => d.OrderId == order.Id || d.SubscriptionId == order.Id)
            .Select(d => new { d.OrderId, d.ServiceDate, d.ServiceSequence }).ToListAsync(ct);
        if (existingDays.Any(d => d.OrderId != order.Id))
            throw new UpcomingMealException(409, "service_day_link_review",
                "Existing subscription service days need an order-link review. They were not duplicated or changed.");
        var existingDates = existingDays.Select(d => d.ServiceDate).ToHashSet();
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
            var qatarToday = SubscriptionMenuRules.QatarToday(now);
            if (date < qatarToday || existingDates.Contains(date)) continue; // Never regenerate or overwrite saved choices.
            if (existingDays.Any(d => d.ServiceSequence == sequence && d.ServiceDate != date))
                throw new UpcomingMealException(409, "service_sequence_review",
                    "An existing service sequence belongs to a different delivery date. Review the schedule before backfilling.");
            var day = new SubscriptionServiceDay
            {
                Id = Guid.NewGuid(), SubscriptionId = order.Id, OrderId = order.Id,
                ServiceDate = date, DayOfWeek = (short)date.DayOfWeek,
                MealPlanTemplateId = template.Id, ServiceSequence = sequence,
                MenuWeekNumber = SubscriptionMenuRules.MenuWeek(sequence, template.CycleWeeks),
                MealCount = mealCount, SnackCount = snackCount,
                SelectionCutoffAt = SubscriptionMenuRules.TomorrowCutoff(date),
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
                false, SubscriptionMenuRules.TomorrowCutoff(d.ServiceDate), reason ?? "Confirm your meals from Tomorrow’s Menu on the dashboard.");
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

    // Old per-card writes must not bypass one-time whole-day confirmation.
    public Task<DailyMealMenuResponse> ReplaceAsync(Guid orderId, Guid dayId, Guid selectionId,
        Guid userId, string language, ReplaceAssignedMealRequest request, CancellationToken ct) =>
        throw new UpcomingMealException(409, "confirmation_required", "Choose your meals on the dashboard and confirm the complete selection.");

    private Task<Order?> OwnedOrderAsync(Guid id, Guid userId, CancellationToken ct) =>
        db.Orders.AsNoTracking().Include(o => o.Meals).Include(o => o.DeliveryDays).SingleOrDefaultAsync(o => o.Id == id && db.CustomerProfiles.Any(p =>
            p.Id == o.CustomerProfileId && p.UserId == userId && p.IsActive), ct);

    private Task<CustomerProfile> ProfileAsync(Guid profileId, CancellationToken ct) =>
        db.CustomerProfiles.AsNoTracking().Include(p => p.Allergens).Include(p => p.Preferences)
            .SingleAsync(p => p.Id == profileId, ct);

    private async Task<List<MealPlanSlotOption>> EligibleAsync(SubscriptionServiceDay day, Order order, CustomerProfile profile, CancellationToken ct)
    {
        if (day.ServiceDate.DayOfWeek == DayOfWeek.Friday || day.MealPlanTemplateId is null ||
            !await db.MealPlanTemplates.AnyAsync(t => t.Id == day.MealPlanTemplateId && t.IsPublished, ct)) return [];
        var weekday = MenuWeekdayExtensions.FromDate(day.ServiceDate);
        var week = day.MenuWeekNumber ?? (day.ServiceSequence is > 0
            ? SubscriptionMenuRules.MenuWeek(day.ServiceSequence.Value, 4) : 0);
        var atDelivery = SubscriptionMenuRules.QatarCutoff(day.ServiceDate, 0, order.DeliveryStartTime);
        var allergenIds = profile.Allergens.Select(a => a.AllergenId).ToArray();
        var options = await db.MealPlanSlotOptions.AsNoTracking()
            .Include(o => o.Slot).ThenInclude(s => s.MealType)
            .Include(o => o.MealItem).ThenInclude(m => m.Translations)
            .Include(o => o.MealItem).ThenInclude(m => m.Nutrition)
            .Where(o => o.Slot.Day.MealPlanTemplateId == day.MealPlanTemplateId &&
                o.Slot.Day.WeekNumber == week && o.Slot.Day.MenuWeekday == weekday &&
                o.Slot.Day.IsActive && o.Slot.IsActive && o.Slot.MaximumSelection > 0 && o.Slot.MealType.IsActive &&
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

    private async Task<DailyMealMenuResponse> MapAsync(SubscriptionServiceDay day, Order order, string language, CancellationToken ct, bool includeEligible = false)
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
        if (!SubscriptionMenuRules.IsSelectionOpen(day.ServiceDate, clock.GetUtcNow()) || (!includeEligible && reason is not null)) alternatives = [];
        return new(order.Id, day.Id, day.ServiceDate, day.MenuWeekNumber ?? 0,
            day.ServiceDate.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)day.ServiceDate.DayOfWeek,
            day.MealCount ?? 0, day.SnackCount ?? 0, Status(day, reason), false,
            SubscriptionMenuRules.TomorrowCutoff(day.ServiceDate), reason ?? "Confirm your meals from Tomorrow’s Menu on the dashboard.", day.Revision,
            saved.Select(s => new AssignedMealResponse(s.Id,
                s.SelectionSource == "DEFAULT" ? "automatic" : s.SelectionSource.ToLowerInvariant(),
                Item(s.MenuItemId, s.MealPlanSlotOptionId ?? Guid.Empty, s.Category ?? "meal", s.MealTypeId))).ToArray(),
            alternatives.Select(o => Item(o.MealItemId, o.Id, Category(o), o.Slot.MealTypeId)).ToArray());
    }

    private string? LockReason(SubscriptionServiceDay day, Order order)
    {
        if (!SubscriptionMenuRules.IsSelectionOpen(day.ServiceDate, clock.GetUtcNow()))
            return "Meal selection is available only for tomorrow before 9:00 PM Qatar time.";
        if (day.Status == "DELIVERED") return "This delivery has been completed.";
        if (order.Status is not ("CONFIRMED" or "ACTIVE")) return "This subscription is not active.";
        if (day.SelectionLocked || day.SelectionStatus == "LOCKED" || day.Status is not ("PLANNED" or "CONFIRMED"))
            return "Your meals are locked for kitchen preparation.";
        if (day.ReviewReason is not null) return day.ReviewReason;
        if (day.Selections.Count(s => s.Category == "meal" && s.Status != "CANCELLED") != day.MealCount ||
            day.Selections.Count(s => s.Category == "snack" && s.Status != "CANCELLED") != day.SnackCount)
            return "Our team needs to review the meal quantities for this day.";
        if (clock.GetUtcNow() >= SubscriptionMenuRules.TomorrowCutoff(day.ServiceDate)) return "The selection deadline has passed. Your meals are locked for kitchen preparation.";
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
