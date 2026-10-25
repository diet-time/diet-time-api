using System.Globalization;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace DietTime.Persistence;

public sealed class GuestHomeService(
    DietTimeDbContext db,
    IMemoryCache cache,
    GuestHomeCacheVersion cacheVersion,
    IStorageUrlService storage,
    IOptions<MenuRotationOptions> rotationOptions) : IGuestHomeService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    private const int GuestCalendarLeadTimeDays = 2;

    public async Task<GuestHomeResponse?> GetAsync(
        GuestHomeQuery request,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var language = request.Language.Trim().ToLowerInvariant();
        var planCode = string.IsNullOrWhiteSpace(request.PlanCode)
            ? null
            : request.PlanCode.Trim().ToUpperInvariant();
        var businessDate = DateOnly.FromDateTime(now.UtcDateTime);
        var calendarStart = businessDate.AddDays(GuestCalendarLeadTimeDays);
        var requestedDate = request.Date ?? calendarStart;

        var plans = await ActivePlans(requestedDate, language).ToListAsync(ct);
        if (plans.Count == 0)
            return null;

        var selectedPlan = planCode is null
            ? plans[0]
            : plans.FirstOrDefault(plan => plan.Code == planCode)
                ?? throw new ArgumentException(
                    $"Unknown or inactive planCode '{request.PlanCode}'.",
                    nameof(request.PlanCode));

        var selectedPlanDays = await db.MealPlanTemplateDays.AsNoTracking()
            .Where(day => day.MealPlanTemplateId == selectedPlan.Id && day.IsActive)
            .Select(day => new DayRow(day.Id, day.WeekNumber, day.MenuWeekday))
            .ToListAsync(ct);

        var selectedDate = requestedDate;
        var selectedDay = FindDay(selectedPlan, selectedPlanDays, selectedDate);

        if (selectedDay is null && request.Date is null)
        {
            for (var offset = 1; offset <= 31 && selectedDay is null; offset++)
            {
                var candidate = requestedDate.AddDays(offset);
                if (selectedPlan.ValidUntil is not null && candidate > selectedPlan.ValidUntil)
                    break;

                selectedDay = FindDay(selectedPlan, selectedPlanDays, candidate);
                if (selectedDay is not null)
                    selectedDate = candidate;
            }
        }

        if (selectedDay is null)
            return null;

        var selectedWeek = ResolveWeek(selectedPlan, selectedDate);
        var cacheKey =
            $"guest-home-summary:{cacheVersion.Current}:{language}:{businessDate:yyyy-MM-dd}:{selectedPlan.Id}:{selectedPlan.VersionNumber}:{selectedPlan.Revision}:{selectedWeek?.ToString() ?? "-"}:{selectedDate:yyyy-MM-dd}";

        if (cache.TryGetValue(cacheKey, out GuestHomeResponse? cached))
            return cached;

        var culture = CultureInfo.GetCultureInfo(language == "ar" ? "ar-QA" : "en-US");
        var weeklyCalendar = Enumerable.Range(0, 7)
            .Select(offset => calendarStart.AddDays(offset))
            .Select(date => new GuestCalendarDayResponse(
                date,
                date.Day,
                culture.DateTimeFormat.GetDayName(date.DayOfWeek),
                culture.DateTimeFormat.GetAbbreviatedDayName(date.DayOfWeek),
                date == businessDate,
                date == selectedDate,
                FindDay(selectedPlan, selectedPlanDays, date) is not null
                    && (selectedPlan.ValidFrom is null || date >= selectedPlan.ValidFrom)
                    && (selectedPlan.ValidUntil is null || date <= selectedPlan.ValidUntil)))
            .ToArray();

        var planIds = plans.Select(plan => plan.Id).ToArray();
        var planDays = await db.MealPlanTemplateDays.AsNoTracking()
            .Where(day => planIds.Contains(day.MealPlanTemplateId) && day.IsActive)
            .Select(day => new PlanDayRow(day.MealPlanTemplateId, day.Id, day.WeekNumber, day.MenuWeekday))
            .ToListAsync(ct);
        var selectedDayIds = plans
            .SelectMany(plan =>
            {
                var week = ResolveWeek(plan, selectedDate);
                return week is null
                    ? []
                    : planDays
                        .Where(day =>
                            day.PlanId == plan.Id &&
                            day.WeekNumber == week &&
                            day.MenuWeekday == MenuWeekdayExtensions.FromDate(selectedDate))
                        .Select(day => day.Id);
            })
            .ToArray();
        var selectedWeekday = MenuWeekdayExtensions.FromDate(selectedDate);
        var slotRows = await db.MealPlanTemplateSlots.AsNoTracking()
            .Where(slot =>
                selectedDayIds.Contains(slot.MealPlanTemplateDayId) &&
                slot.Day.MenuWeekday == selectedWeekday &&
                slot.IsActive &&
                slot.MealType.IsActive &&
                slot.Options.Any(option =>
                    option.IsAvailable &&
                    (option.AvailableFrom == null || option.AvailableFrom <= now) &&
                    (option.AvailableUntil == null || option.AvailableUntil > now) &&
                    option.MealItem.Status == "ACTIVE" &&
                    option.MealItem.IsAvailable &&
                    (option.MealItem.AvailableFrom == null || option.MealItem.AvailableFrom <= now) &&
                    (option.MealItem.AvailableUntil == null || option.MealItem.AvailableUntil > now)))
            .OrderBy(slot => slot.MealType.DisplayOrder)
            .ThenBy(slot => slot.DisplayOrder)
            .ThenBy(slot => slot.Id)
            .Select(slot => new SlotRow(
                slot.Day.MealPlanTemplateId,
                slot.Id,
                slot.MealType.Id,
                slot.MealType.Code,
                slot.MealType.Translations
                    .Where(t => t.LanguageCode.ToLower() == language)
                    .Select(t => t.Name)
                    .FirstOrDefault()
                    ?? slot.MealType.Translations
                        .Where(t => t.LanguageCode.ToLower() == "en")
                        .Select(t => t.Name)
                        .FirstOrDefault()
                    ?? slot.MealType.Translations.Select(t => t.Name).FirstOrDefault()
                    ?? slot.MealType.Code,
                slot.MealType.DisplayOrder,
                slot.DisplayOrder,
                slot.MinimumSelection,
                slot.MaximumSelection,
                slot.IsRequired))
            .ToListAsync(ct);

        var response = new GuestHomeResponse(
            plans.Select(plan => new GuestPlanSummaryResponse(
                plan.Id,
                plan.Code,
                plan.Name,
                plan.Description,
                ResolveImage(plan.ImageUrl, plan.ImageObjectKey),
                null,
                plan.DisplayOrder,
                plan.Id == selectedPlan.Id,
                slotRows
                    .Where(slot => slot.PlanId == plan.Id)
                    .OrderBy(slot => slot.MealTimeDisplayOrder)
                    .ThenBy(slot => slot.DisplayOrder)
                    .ThenBy(slot => slot.Id)
                    .Select(ToSlotResponse)
                    .ToArray(),
                plan.VersionNumber,
                plan.Revision,
                ResolveWeek(plan, selectedDate) ?? 1))
                .ToArray(),
            weeklyCalendar);

        cache.Set(cacheKey, response, CacheDuration);
        return response;
    }

    public async Task<GuestMenuResponse?> GetMenuAsync(
        string planCode,
        GuestMenuQuery request,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var language = request.Language.Trim().ToLowerInvariant();
        var normalizedPlanCode = planCode.Trim().ToUpperInvariant();

        var plan = await ActivePlans(request.Date, language, normalizedPlanCode)
            .FirstOrDefaultAsync(ct);
        if (plan is null)
            return null;

        var week = ResolveWeek(plan, request.Date);
        if (week is null)
            return null;

        var cacheKey =
            $"guest-menu:{cacheVersion.Current}:{language}:{plan.Id}:{plan.VersionNumber}:{plan.Revision}:{request.Date:yyyy-MM-dd}:{week.Value}";

        if (cache.TryGetValue(cacheKey, out GuestMenuResponse? cached))
            return cached;

        var weekday = MenuWeekdayExtensions.FromDate(request.Date);
        var dayId = await db.MealPlanTemplateDays.AsNoTracking()
            .Where(day =>
                day.MealPlanTemplateId == plan.Id &&
                day.IsActive &&
                day.WeekNumber == week &&
                day.MenuWeekday == weekday)
            .Select(day => (Guid?)day.Id)
            .FirstOrDefaultAsync(ct);
        if (dayId is null)
            return null;

        var mealRows = await ProjectMealRows(
                AvailableMealOptions([dayId.Value], now)
                    .OrderBy(option => option.Slot.DisplayOrder)
                    .ThenBy(option => option.DisplayOrder),
                language)
            .ToListAsync(ct);

        var response = new GuestMenuResponse(
            plan.Id,
            plan.Code,
            request.Date,
            BuildSlots(mealRows),
            plan.VersionNumber,
            plan.Revision,
            week.Value);
        cache.Set(cacheKey, response, CacheDuration);
        return response;
    }

    public async Task<IReadOnlyList<GuestAllergenLookupResponse>> GetAllergensAsync(
        string language,
        CancellationToken ct)
    {
        var normalizedLanguage = language.Trim().ToLowerInvariant();
        var cacheKey = $"guest-allergens:{cacheVersion.Current}:{normalizedLanguage}";

        if (cache.TryGetValue(cacheKey, out IReadOnlyList<GuestAllergenLookupResponse>? cached) &&
            cached is not null)
        {
            return cached;
        }

        var allergens = await db.Allergens.AsNoTracking()
            .Where(allergen => allergen.IsActive)
            .OrderBy(allergen => allergen.Code)
            .Select(allergen => new GuestAllergenLookupResponse(
                allergen.Id,
                allergen.Code,
                allergen.Translations
                    .Where(translation => translation.LanguageCode.ToLower() == normalizedLanguage)
                    .Select(translation => translation.Name)
                    .FirstOrDefault()
                    ?? allergen.Translations
                        .Where(translation => translation.LanguageCode.ToLower() == "en")
                        .Select(translation => translation.Name)
                        .FirstOrDefault()
                    ?? allergen.Translations.Select(translation => translation.Name).FirstOrDefault()
                    ?? allergen.Code))
            .ToArrayAsync(ct);

        cache.Set(cacheKey, allergens, CacheDuration);
        return allergens;
    }

    private IQueryable<PlanRow> ActivePlans(
        DateOnly date,
        string language,
        string? planCode = null)
    {
        var plans = db.MealPlanTemplates.AsNoTracking()
            .Where(plan =>
                plan.IsActive &&
                plan.IsPublished &&
                !db.MealPlanTemplates.Any(version =>
                    version.VersionGroupId == plan.VersionGroupId &&
                    version.IsPublished &&
                    version.VersionNumber > plan.VersionNumber) &&
                (plan.ValidFrom == null || plan.ValidFrom <= date) &&
                (plan.ValidUntil == null || plan.ValidUntil >= date));

        if (planCode is not null)
            plans = plans.Where(plan => plan.Code == planCode);

        return plans
            .OrderBy(plan => plan.Days
                .Where(day => day.IsActive)
                .Select(day => (int?)day.DisplayOrder)
                .Min() ?? int.MaxValue)
            .ThenBy(plan => plan.Code)
            .Select(plan => new PlanRow(
                plan.Id,
                plan.Code,
                plan.Days
                    .Where(day => day.IsActive)
                    .Select(day => (int?)day.DisplayOrder)
                    .Min() ?? int.MaxValue,
                db.MealMedia
                    .Where(media =>
                        media.Status == "ACTIVE" &&
                        media.MediaType == MealMediaTypes.MealPlan &&
                        db.MealPlanTemplates.Any(mediaPlan =>
                            mediaPlan.Id == media.EntityId &&
                            mediaPlan.VersionGroupId == plan.VersionGroupId))
                    .OrderByDescending(media => media.EntityId == plan.Id)
                    .ThenByDescending(media => media.IsPrimary)
                    .ThenByDescending(media => media.UpdatedAt)
                    .ThenBy(media => media.DisplayOrder)
                    .Select(media => media.PublicUrl)
                    .FirstOrDefault(),
                db.MealMedia
                    .Where(media =>
                        media.Status == "ACTIVE" &&
                        media.MediaType == MealMediaTypes.MealPlan &&
                        db.MealPlanTemplates.Any(mediaPlan =>
                            mediaPlan.Id == media.EntityId &&
                            mediaPlan.VersionGroupId == plan.VersionGroupId))
                    .OrderByDescending(media => media.EntityId == plan.Id)
                    .ThenByDescending(media => media.IsPrimary)
                    .ThenByDescending(media => media.UpdatedAt)
                    .ThenBy(media => media.DisplayOrder)
                    .Select(media => media.ObjectKey)
                    .FirstOrDefault(),
                plan.ValidFrom,
                plan.ValidUntil,
                plan.VersionNumber,
                plan.RowVersion,
                plan.CycleWeeks,
                plan.Translations
                    .Where(t => t.LanguageCode.ToLower() == language && t.Name != "")
                    .Select(t => t.Name)
                    .FirstOrDefault()
                    ?? plan.Translations
                        .Where(t => t.LanguageCode.ToLower() == "en" && t.Name != "")
                        .Select(t => t.Name)
                        .FirstOrDefault()
                    ?? plan.Translations.Select(t => t.Name).FirstOrDefault()
                    ?? plan.Code,
                plan.Translations
                    .Where(t =>
                        t.LanguageCode.ToLower() == language &&
                        t.ShortDescription != null &&
                        t.ShortDescription != "")
                    .Select(t => t.ShortDescription)
                    .FirstOrDefault()
                    ?? plan.Translations
                        .Where(t =>
                            t.LanguageCode.ToLower() == "en" &&
                            t.ShortDescription != null &&
                            t.ShortDescription != "")
                        .Select(t => t.ShortDescription)
                        .FirstOrDefault()
                    ?? plan.Translations
                        .Where(t => t.ShortDescription != null && t.ShortDescription != "")
                        .Select(t => t.ShortDescription)
                        .FirstOrDefault()
                    ?? string.Empty));
    }

    private int? ResolveWeek(PlanRow plan, DateOnly date) =>
        MenuRotationResolver.ResolveWeek(
            date,
            plan.CycleWeeks,
            plan.ValidFrom,
            rotationOptions.Value.AnchorDate);

    private DayRow? FindDay(PlanRow plan, IEnumerable<DayRow> days, DateOnly date)
    {
        var week = ResolveWeek(plan, date);
        return week is null
            ? null
            : days.FirstOrDefault(day =>
                day.WeekNumber == week &&
                day.MenuWeekday == MenuWeekdayExtensions.FromDate(date));
    }

    private IQueryable<MealPlanSlotOption> AvailableMealOptions(
        IReadOnlyCollection<Guid> templateDayIds,
        DateTimeOffset now) =>
        db.MealPlanSlotOptions.AsNoTracking()
            .Where(option =>
                templateDayIds.Contains(option.Slot.MealPlanTemplateDayId) &&
                option.Slot.IsActive &&
                option.Slot.MealType.IsActive &&
                option.IsAvailable &&
                (option.AvailableFrom == null || option.AvailableFrom <= now) &&
                (option.AvailableUntil == null || option.AvailableUntil > now) &&
                option.MealItem.Status == "ACTIVE" &&
                option.MealItem.IsAvailable &&
                (option.MealItem.AvailableFrom == null || option.MealItem.AvailableFrom <= now) &&
                (option.MealItem.AvailableUntil == null || option.MealItem.AvailableUntil > now));

    private IQueryable<MealRow> ProjectMealRows(
        IQueryable<MealPlanSlotOption> query,
        string language) =>
        query.Select(option => new MealRow(
            option.MealItemId,
            option.Slot.MealPlanTemplateDayId,
            option.MealPlanTemplateSlotId,
            option.Slot.DisplayOrder,
            option.Slot.MinimumSelection,
            option.Slot.MaximumSelection,
            option.Slot.IsRequired,
            option.Slot.MealType.Id,
            option.Slot.MealType.DisplayOrder,
            option.MealItem.Sku,
            option.MealItem.Translations
                .Where(t => t.LanguageCode == language)
                .Select(t => t.Name)
                .FirstOrDefault()
                ?? option.MealItem.Translations
                    .Where(t => t.LanguageCode == "en")
                    .Select(t => t.Name)
                    .FirstOrDefault()
                ?? option.MealItem.Translations.Select(t => t.Name).FirstOrDefault()
                ?? option.MealItem.Sku,
            option.MealItem.Translations
                .Where(t => t.LanguageCode == language)
                .Select(t => t.FullDescription ?? t.ShortDescription)
                .FirstOrDefault()
                ?? option.MealItem.Translations
                    .Where(t => t.LanguageCode == "en")
                    .Select(t => t.FullDescription ?? t.ShortDescription)
                    .FirstOrDefault()
                ?? option.MealItem.Translations
                    .Select(t => t.FullDescription ?? t.ShortDescription)
                    .FirstOrDefault()
                ?? option.MealItem.Sku,
            db.MealMedia
                .Where(media =>
                    media.EntityId == option.MealItemId &&
                    media.Status == "ACTIVE" &&
                    media.MediaType == MealMediaTypes.MealItem)
                .OrderByDescending(media => media.IsPrimary)
                .ThenBy(media => media.DisplayOrder)
                .Select(media => media.PublicUrl)
                .FirstOrDefault(),
            db.MealMedia
                .Where(media =>
                    media.EntityId == option.MealItemId &&
                    media.Status == "ACTIVE" &&
                    media.MediaType == MealMediaTypes.MealItem)
                .OrderByDescending(media => media.IsPrimary)
                .ThenBy(media => media.DisplayOrder)
                .Select(media => media.ThumbnailUrl ?? media.PublicUrl)
                .FirstOrDefault(),
            option.Slot.MealType.Code,
            option.Slot.MealType.Translations
                .Where(t => t.LanguageCode == language)
                .Select(t => t.Name)
                .FirstOrDefault()
                ?? option.Slot.MealType.Translations
                    .Where(t => t.LanguageCode == "en")
                    .Select(t => t.Name)
                    .FirstOrDefault()
                ?? option.Slot.MealType.Translations.Select(t => t.Name).FirstOrDefault()
                ?? option.Slot.MealType.Code,
            option.MealItem.Nutrition == null
                ? null
                : option.MealItem.Nutrition.CaloriesKcal,
            option.MealItem.Nutrition == null
                ? null
                : option.MealItem.Nutrition.ProteinGrams,
            option.MealItem.Nutrition == null
                ? null
                : option.MealItem.Nutrition.CarbohydratesGrams,
            option.MealItem.Nutrition == null
                ? null
                : option.MealItem.Nutrition.FatGrams,
            option.MealItem.Nutrition == null
                ? null
                : option.MealItem.Nutrition.FiberGrams,
            option.DisplayOrder,
            option.MealItem.Allergens
                .Where(allergen => allergen.Allergen.IsActive)
                .OrderBy(allergen => allergen.Allergen.Code)
                .Select(allergen => new GuestCodeNameResponse(
                    allergen.Allergen.Code,
                    allergen.Allergen.Translations
                        .Where(t => t.LanguageCode == language)
                        .Select(t => t.Name)
                        .FirstOrDefault()
                        ?? allergen.Allergen.Translations
                            .Where(t => t.LanguageCode == "en")
                            .Select(t => t.Name)
                            .FirstOrDefault()
                        ?? allergen.Allergen.Translations
                            .Select(t => t.Name)
                            .FirstOrDefault()
                        ?? allergen.Allergen.Code))
                .ToList()));

    private static IReadOnlyList<GuestMealSlotResponse> BuildSlots(
        IEnumerable<MealRow> mealRows) =>
        mealRows
            .GroupBy(row => new
            {
                row.SlotId,
                row.SlotDisplayOrder,
                row.MinimumSelection,
                row.MaximumSelection,
                row.IsRequired,
                row.MealTimeId,
                row.MealTimeCode,
                row.MealTimeName,
                row.MealTimeDisplayOrder
            })
            .OrderBy(group => group.Key.SlotDisplayOrder)
            .Select(group => new GuestMealSlotResponse(
                group.Key.SlotId,
                new GuestSlotMealTimeResponse(
                    group.Key.MealTimeId,
                    group.Key.MealTimeCode,
                    group.Key.MealTimeName,
                    group.Key.MealTimeDisplayOrder),
                group.Key.SlotDisplayOrder,
                group.Key.MinimumSelection,
                group.Key.MaximumSelection,
                group.Key.IsRequired,
                group.OrderBy(row => row.DisplayOrder)
                    .Select(row => new GuestMealResponse(
                        row.Id,
                        row.Code,
                        row.Name,
                        row.Description,
                        row.ImageUrl,
                        row.ThumbnailUrl,
                        new GuestNutritionResponse(
                            row.Calories,
                            row.Protein,
                            row.Carbs,
                            row.Fat,
                            row.Fiber),
                        [],
                        row.Allergens,
                        true,
                        row.DisplayOrder))
                    .ToArray()))
            .ToArray();

    private static GuestSlotResponse ToSlotResponse(SlotRow slot) =>
        new(
            slot.Id,
            new GuestSlotMealTimeResponse(
                slot.MealTimeId,
                slot.MealTimeCode,
                slot.MealTimeName,
                slot.MealTimeDisplayOrder),
            slot.DisplayOrder,
            slot.MinimumSelection,
            slot.MaximumSelection,
            slot.IsRequired);

    private string? ResolveImage(string? publicUrl, string? objectKey) =>
        !string.IsNullOrWhiteSpace(publicUrl)
            ? publicUrl
            : string.IsNullOrWhiteSpace(objectKey)
                ? null
                : storage.GetPublicUrl(objectKey);

    private sealed record PlanRow(
        Guid Id,
        string Code,
        int DisplayOrder,
        string? ImageUrl,
        string? ImageObjectKey,
        DateOnly? ValidFrom,
        DateOnly? ValidUntil,
        int VersionNumber,
        long Revision,
        int CycleWeeks,
        string Name,
        string Description);

    private sealed record DayRow(Guid Id, int WeekNumber, MenuWeekday MenuWeekday);

    private sealed record PlanDayRow(Guid PlanId, Guid Id, int WeekNumber, MenuWeekday MenuWeekday);

    private sealed record SlotRow(
        Guid PlanId,
        Guid Id,
        Guid MealTimeId,
        string MealTimeCode,
        string MealTimeName,
        int MealTimeDisplayOrder,
        int DisplayOrder,
        int MinimumSelection,
        int MaximumSelection,
        bool IsRequired);

    private sealed record MealRow(
        Guid Id,
        Guid TemplateDayId,
        Guid SlotId,
        int SlotDisplayOrder,
        int MinimumSelection,
        int MaximumSelection,
        bool IsRequired,
        Guid MealTimeId,
        int MealTimeDisplayOrder,
        string Code,
        string Name,
        string Description,
        string? ImageUrl,
        string? ThumbnailUrl,
        string MealTimeCode,
        string MealTimeName,
        decimal? Calories,
        decimal? Protein,
        decimal? Carbs,
        decimal? Fat,
        decimal? Fiber,
        int DisplayOrder,
        IReadOnlyList<GuestCodeNameResponse> Allergens);
}
