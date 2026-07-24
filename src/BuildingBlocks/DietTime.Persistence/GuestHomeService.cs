using System.Globalization;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace DietTime.Persistence;

public sealed class GuestHomeService(
    DietTimeDbContext db,
    IMemoryCache cache,
    GuestHomeCacheVersion cacheVersion,
    IStorageUrlService storage) : IGuestHomeService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    public async Task<GuestHomeResponse?> GetAsync(
        GuestHomeQuery request,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var language = request.Language.Trim().ToLowerInvariant();
        var mealTimeCode = request.MealTimeCode.Trim().ToUpperInvariant();
        var planCode = string.IsNullOrWhiteSpace(request.PlanCode)
            ? null
            : request.PlanCode.Trim().ToUpperInvariant();
        var businessDate = DateOnly.FromDateTime(now.UtcDateTime);
        var requestedDate = request.Date ?? businessDate;
        var cacheKey =
            $"guest-home:{cacheVersion.Current}:{language}:{requestedDate:yyyy-MM-dd}:{planCode ?? "-"}:{mealTimeCode}:{request.Page}:{request.PageSize}:{request.IncludeAll}";

        if (cache.TryGetValue(cacheKey, out GuestHomeResponse? cached))
            return cached;

        var plans = await db.MealPlanTemplates.AsNoTracking()
            .Where(p =>
                p.IsActive &&
                p.IsPublished &&
                !db.MealPlanTemplates.Any(version =>
                    version.VersionGroupId == p.VersionGroupId &&
                    version.IsPublished &&
                    version.VersionNumber > p.VersionNumber) &&
                (p.ValidFrom == null || p.ValidFrom <= requestedDate) &&
                (p.ValidUntil == null || p.ValidUntil >= requestedDate))
            .OrderBy(p => p.Days
                .Where(d => d.IsActive)
                .Select(d => (int?)d.DisplayOrder)
                .Min() ?? int.MaxValue)
            .ThenBy(p => p.Code)
            .Select(p => new PlanRow(
                p.Id,
                p.Code,
                p.Days
                    .Where(d => d.IsActive)
                    .Select(d => (int?)d.DisplayOrder)
                    .Min() ?? int.MaxValue,
                db.MealMedia
                    .Where(m =>
                        m.Status == "ACTIVE" &&
                        m.MediaType == MealMediaTypes.MealPlan &&
                        db.MealPlanTemplates.Any(mediaPlan =>
                            mediaPlan.Id == m.EntityId &&
                            mediaPlan.VersionGroupId == p.VersionGroupId))
                    .OrderByDescending(m => m.EntityId == p.Id)
                    .ThenByDescending(m => m.IsPrimary)
                    .ThenByDescending(m => m.UpdatedAt)
                    .ThenBy(m => m.DisplayOrder)
                    .Select(m => m.PublicUrl)
                    .FirstOrDefault(),
                db.MealMedia
                    .Where(m =>
                        m.Status == "ACTIVE" &&
                        m.MediaType == MealMediaTypes.MealPlan &&
                        db.MealPlanTemplates.Any(mediaPlan =>
                            mediaPlan.Id == m.EntityId &&
                            mediaPlan.VersionGroupId == p.VersionGroupId))
                    .OrderByDescending(m => m.EntityId == p.Id)
                    .ThenByDescending(m => m.IsPrimary)
                    .ThenByDescending(m => m.UpdatedAt)
                    .ThenBy(m => m.DisplayOrder)
                    .Select(m => m.ObjectKey)
                    .FirstOrDefault(),
                p.ValidFrom,
                p.ValidUntil,
                p.Translations.Where(t => t.LanguageCode.ToLower() == language && t.Name != "").Select(t => t.Name).FirstOrDefault()
                    ?? p.Translations.Where(t => t.LanguageCode.ToLower() == "en" && t.Name != "").Select(t => t.Name).FirstOrDefault()
                    ?? p.Translations.Select(t => t.Name).FirstOrDefault()
                    ?? p.Code,
                p.Translations
                    .Where(t => t.LanguageCode.ToLower() == language && t.ShortDescription != null && t.ShortDescription != "")
                    .Select(t => t.ShortDescription)
                    .FirstOrDefault()
                    ?? p.Translations
                        .Where(t => t.LanguageCode.ToLower() == "en" && t.ShortDescription != null && t.ShortDescription != "")
                        .Select(t => t.ShortDescription)
                        .FirstOrDefault()
                    ?? p.Translations
                        .Where(t => t.ShortDescription != null && t.ShortDescription != "")
                        .Select(t => t.ShortDescription)
                        .FirstOrDefault()
                    ?? string.Empty))
            .ToListAsync(ct);

        if (plans.Count == 0)
            return null;

        var selectedPlan = planCode is null
            ? plans[0]
            : plans.FirstOrDefault(p => p.Code == planCode)
                ?? throw new ArgumentException($"Unknown or inactive planCode '{request.PlanCode}'.", nameof(request.PlanCode));

        var templateDays = await db.MealPlanTemplateDays.AsNoTracking()
            .Where(d => d.MealPlanTemplateId == selectedPlan.Id && d.IsActive)
            .Select(d => new { d.Id, d.MenuWeekday })
            .ToListAsync(ct);

        var selectedDate = requestedDate;
        var selectedDay = templateDays.FirstOrDefault(d =>
            d.MenuWeekday == MenuWeekdayExtensions.FromDate(selectedDate));

        if (selectedDay is null && request.Date is null)
        {
            for (var offset = 1; offset <= 31 && selectedDay is null; offset++)
            {
                var candidate = requestedDate.AddDays(offset);
                if (selectedPlan.ValidUntil is not null && candidate > selectedPlan.ValidUntil)
                    break;
                selectedDay = templateDays.FirstOrDefault(d =>
                    d.MenuWeekday == MenuWeekdayExtensions.FromDate(candidate));
                if (selectedDay is not null)
                    selectedDate = candidate;
            }
        }

        if (selectedDay is null)
            return null;

        var culture = CultureInfo.GetCultureInfo(language == "ar" ? "ar-QA" : "en-US");
        var weeklyCalendar = Enumerable.Range(0, 7)
            .Select(offset => selectedDate.AddDays(offset))
            .Select(date => new GuestCalendarDayResponse(
                date,
                date.Day,
                culture.DateTimeFormat.GetDayName(date.DayOfWeek),
                culture.DateTimeFormat.GetAbbreviatedDayName(date.DayOfWeek),
                date == businessDate,
                date == selectedDate,
                templateDays.Any(d => d.MenuWeekday == MenuWeekdayExtensions.FromDate(date))
                    && (selectedPlan.ValidFrom is null || date >= selectedPlan.ValidFrom)
                    && (selectedPlan.ValidUntil is null || date <= selectedPlan.ValidUntil)))
            .ToArray();

        var mealTypes = await db.MealTypes.AsNoTracking()
            .Where(t => t.IsActive &&
                (t.Code == "BREAKFAST" || t.Code == "LUNCH" || t.Code == "DINNER" ||
                    t.Code == "SNACK" || t.Code == "SNACK_DESSERT"))
            .OrderBy(t => t.DisplayOrder)
            .Select(t => new GuestMealTimeResponse(
                t.Id,
                t.Code,
                t.Translations.Where(x => x.LanguageCode == language).Select(x => x.Name).FirstOrDefault()
                    ?? t.Translations.Where(x => x.LanguageCode == "en").Select(x => x.Name).FirstOrDefault()
                    ?? t.Translations.Select(x => x.Name).FirstOrDefault()
                    ?? t.Code,
                null,
                t.DisplayOrder,
                t.Code == mealTimeCode))
            .ToListAsync(ct);
        mealTypes.Insert(0, new GuestMealTimeResponse(
            null,
            "ALL",
            language == "ar" ? "الكل" : "All",
            null,
            0,
            mealTimeCode == "ALL"));

        var mealQuery = AvailableMealOptions([selectedDay.Id], now);

        if (mealTimeCode != "ALL")
            mealQuery = mealQuery.Where(o =>
                o.Slot.MealType.Code == mealTimeCode ||
                (mealTimeCode == "SNACK" && o.Slot.MealType.Code == "SNACK_DESSERT"));

        var totalRecords = await mealQuery.CountAsync(ct);
        var totalPages = (int)Math.Ceiling(totalRecords / (double)request.PageSize);
        var mealRows = await ProjectMealRows(mealQuery, language)
            .OrderBy(row => row.SlotDisplayOrder)
            .ThenBy(row => row.DisplayOrder)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(ct);

        var slots = BuildSlots(mealRows);

        IReadOnlyList<GuestMenuDayResponse>? menus = null;
        if (request.IncludeAll)
        {
            var planIds = plans.Select(p => p.Id).ToArray();
            var allPlanDays = await db.MealPlanTemplateDays.AsNoTracking()
                .Where(day => planIds.Contains(day.MealPlanTemplateId) && day.IsActive)
                .Select(day => new { day.Id, day.MealPlanTemplateId, day.MenuWeekday })
                .ToListAsync(ct);
            var menuTargets = (
                from plan in plans
                from calendarDay in weeklyCalendar
                where (plan.ValidFrom is null || calendarDay.Date >= plan.ValidFrom)
                    && (plan.ValidUntil is null || calendarDay.Date <= plan.ValidUntil)
                let templateDay = allPlanDays.FirstOrDefault(day =>
                    day.MealPlanTemplateId == plan.Id &&
                    day.MenuWeekday == MenuWeekdayExtensions.FromDate(calendarDay.Date))
                where templateDay is not null
                select new MenuTarget(plan.Code, calendarDay.Date, templateDay.Id))
                .ToArray();
            var allDayIds = menuTargets.Select(target => target.DayId).Distinct().ToArray();
            var allRows = allDayIds.Length == 0
                ? new List<MealRow>()
                : await ProjectMealRows(AvailableMealOptions(allDayIds, now), language)
                    .OrderBy(row => row.SlotDisplayOrder)
                    .ThenBy(row => row.DisplayOrder)
                    .ToListAsync(ct);
            menus = menuTargets
                .Select(target => new GuestMenuDayResponse(
                    target.PlanCode,
                    target.Date,
                    BuildSlots(allRows.Where(row => row.TemplateDayId == target.DayId))))
                .ToArray();
        }

        var response = new GuestHomeResponse(
            plans.Select(p => new GuestPlanResponse(
                p.Id,
                p.Code,
                p.Name,
                p.Description,
                ResolveImage(p.ImageUrl, p.ImageObjectKey),
                null,
                p.DisplayOrder,
                p.Id == selectedPlan.Id,
                p.Id == selectedPlan.Id ? slots : [])).ToArray(),
            weeklyCalendar,
            mealTypes,
            new GuestPaginationResponse(
                request.Page,
                request.PageSize,
                totalRecords,
                totalPages,
                request.Page < totalPages,
                request.Page > 1),
            menus);

        cache.Set(cacheKey, response, CacheDuration);
        return response;
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
            option.MealItem.Translations.Where(t => t.LanguageCode == language).Select(t => t.Name).FirstOrDefault()
                ?? option.MealItem.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault()
                ?? option.MealItem.Translations.Select(t => t.Name).FirstOrDefault()
                ?? option.MealItem.Sku,
            option.MealItem.Translations.Where(t => t.LanguageCode == language).Select(t => t.FullDescription ?? t.ShortDescription).FirstOrDefault()
                ?? option.MealItem.Translations.Where(t => t.LanguageCode == "en").Select(t => t.FullDescription ?? t.ShortDescription).FirstOrDefault()
                ?? option.MealItem.Translations.Select(t => t.FullDescription ?? t.ShortDescription).FirstOrDefault()
                ?? option.MealItem.Sku,
            db.MealMedia
                .Where(media => media.EntityId == option.MealItemId && media.Status == "ACTIVE" &&
                    media.MediaType == MealMediaTypes.MealItem)
                .OrderByDescending(media => media.IsPrimary)
                .ThenBy(media => media.DisplayOrder)
                .Select(media => media.PublicUrl)
                .FirstOrDefault(),
            db.MealMedia
                .Where(media => media.EntityId == option.MealItemId && media.Status == "ACTIVE" &&
                    media.MediaType == MealMediaTypes.MealItem)
                .OrderByDescending(media => media.IsPrimary)
                .ThenBy(media => media.DisplayOrder)
                .Select(media => media.ThumbnailUrl ?? media.PublicUrl)
                .FirstOrDefault(),
            option.Slot.MealType.Code,
            option.Slot.MealType.Translations.Where(t => t.LanguageCode == language).Select(t => t.Name).FirstOrDefault()
                ?? option.Slot.MealType.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault()
                ?? option.Slot.MealType.Translations.Select(t => t.Name).FirstOrDefault()
                ?? option.Slot.MealType.Code,
            option.MealItem.Nutrition == null ? null : option.MealItem.Nutrition.CaloriesKcal,
            option.MealItem.Nutrition == null ? null : option.MealItem.Nutrition.ProteinGrams,
            option.MealItem.Nutrition == null ? null : option.MealItem.Nutrition.CarbohydratesGrams,
            option.MealItem.Nutrition == null ? null : option.MealItem.Nutrition.FatGrams,
            option.MealItem.Nutrition == null ? null : option.MealItem.Nutrition.FiberGrams,
            option.DisplayOrder,
            option.MealItem.Allergens
                .Where(allergen => allergen.Allergen.IsActive)
                .OrderBy(allergen => allergen.Allergen.Code)
                .Select(allergen => new GuestCodeNameResponse(
                    allergen.Allergen.Code,
                    allergen.Allergen.Translations.Where(t => t.LanguageCode == language).Select(t => t.Name).FirstOrDefault()
                        ?? allergen.Allergen.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault()
                        ?? allergen.Allergen.Translations.Select(t => t.Name).FirstOrDefault()
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
                group.OrderBy(row => row.DisplayOrder).Select(row => new GuestMealResponse(
                    row.Id,
                    row.Code,
                    row.Name,
                    row.Description,
                    row.ImageUrl,
                    row.ThumbnailUrl,
                    new GuestNutritionResponse(row.Calories, row.Protein, row.Carbs, row.Fat, row.Fiber),
                    [],
                    row.Allergens,
                    true,
                    row.DisplayOrder)).ToArray()))
            .ToArray();

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
        string Name,
        string Description);

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

    private sealed record MenuTarget(string PlanCode, DateOnly Date, Guid DayId);
}
