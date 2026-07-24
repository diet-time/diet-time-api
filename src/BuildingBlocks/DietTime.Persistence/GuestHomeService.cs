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
    GuestHomeCacheVersion cacheVersion) : IGuestHomeService
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
            $"guest-home:{cacheVersion.Current}:{language}:{requestedDate:yyyy-MM-dd}:{planCode ?? "-"}:{mealTimeCode}:{request.Page}:{request.PageSize}";

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
                p.Days
                    .Where(d => d.IsActive)
                    .SelectMany(d => d.Slots)
                    .Where(s => s.IsActive)
                    .SelectMany(s => s.Options)
                    .Where(o => o.IsAvailable)
                    .SelectMany(o => o.MealItem.Media)
                    .Where(m => m.Status == "ACTIVE" && m.MediaType == "IMAGE")
                    .OrderByDescending(m => m.IsPrimary)
                    .ThenBy(m => m.DisplayOrder)
                    .Select(m => m.PublicUrl)
                    .FirstOrDefault(),
                p.ValidFrom,
                p.ValidUntil,
                p.Translations.Where(t => t.LanguageCode == language).Select(t => t.Name).FirstOrDefault()
                    ?? p.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault()
                    ?? p.Translations.Select(t => t.Name).FirstOrDefault()
                    ?? p.Code,
                p.Translations.Where(t => t.LanguageCode == language).Select(t => t.FullDescription ?? t.ShortDescription).FirstOrDefault()
                    ?? p.Translations.Where(t => t.LanguageCode == "en").Select(t => t.FullDescription ?? t.ShortDescription).FirstOrDefault()
                    ?? p.Translations.Select(t => t.FullDescription ?? t.ShortDescription).FirstOrDefault()
                    ?? p.Code))
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
                (t.Code == "BREAKFAST" || t.Code == "LUNCH" || t.Code == "DINNER" || t.Code == "SNACK"))
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

        var mealQuery = db.MealPlanSlotOptions.AsNoTracking()
            .Where(o =>
                o.MealPlanTemplateSlotId != Guid.Empty &&
                o.Slot.MealPlanTemplateDayId == selectedDay.Id &&
                o.Slot.IsActive &&
                o.Slot.MealType.IsActive &&
                o.IsAvailable &&
                (o.AvailableFrom == null || o.AvailableFrom <= now) &&
                (o.AvailableUntil == null || o.AvailableUntil > now) &&
                o.MealItem.Status == "ACTIVE" &&
                o.MealItem.IsAvailable &&
                (o.MealItem.AvailableFrom == null || o.MealItem.AvailableFrom <= now) &&
                (o.MealItem.AvailableUntil == null || o.MealItem.AvailableUntil > now));

        if (mealTimeCode != "ALL")
            mealQuery = mealQuery.Where(o => o.Slot.MealType.Code == mealTimeCode);

        var totalRecords = await mealQuery.CountAsync(ct);
        var totalPages = (int)Math.Ceiling(totalRecords / (double)request.PageSize);
        var mealRows = await mealQuery
            .OrderBy(o => o.Slot.DisplayOrder)
            .ThenBy(o => o.DisplayOrder)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(o => new MealRow(
                o.MealItemId,
                o.MealItem.Sku,
                o.MealItem.Translations.Where(t => t.LanguageCode == language).Select(t => t.Name).FirstOrDefault()
                    ?? o.MealItem.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault()
                    ?? o.MealItem.Translations.Select(t => t.Name).FirstOrDefault()
                    ?? o.MealItem.Sku,
                o.MealItem.Translations.Where(t => t.LanguageCode == language).Select(t => t.FullDescription ?? t.ShortDescription).FirstOrDefault()
                    ?? o.MealItem.Translations.Where(t => t.LanguageCode == "en").Select(t => t.FullDescription ?? t.ShortDescription).FirstOrDefault()
                    ?? o.MealItem.Translations.Select(t => t.FullDescription ?? t.ShortDescription).FirstOrDefault()
                    ?? o.MealItem.Sku,
                o.MealItem.Media
                    .Where(m => m.Status == "ACTIVE" && m.MediaType == "IMAGE")
                    .OrderByDescending(m => m.IsPrimary)
                    .ThenBy(m => m.DisplayOrder)
                    .Select(m => m.PublicUrl)
                    .FirstOrDefault(),
                o.MealItem.Media
                    .Where(m => m.Status == "ACTIVE" && m.MediaType == "IMAGE")
                    .OrderByDescending(m => m.IsPrimary)
                    .ThenBy(m => m.DisplayOrder)
                    .Select(m => m.ThumbnailUrl ?? m.PublicUrl)
                    .FirstOrDefault(),
                o.Slot.MealType.Code,
                o.Slot.MealType.Translations.Where(t => t.LanguageCode == language).Select(t => t.Name).FirstOrDefault()
                    ?? o.Slot.MealType.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault()
                    ?? o.Slot.MealType.Translations.Select(t => t.Name).FirstOrDefault()
                    ?? o.Slot.MealType.Code,
                o.MealItem.Nutrition == null ? null : o.MealItem.Nutrition.CaloriesKcal,
                o.MealItem.Nutrition == null ? null : o.MealItem.Nutrition.ProteinGrams,
                o.MealItem.Nutrition == null ? null : o.MealItem.Nutrition.CarbohydratesGrams,
                o.MealItem.Nutrition == null ? null : o.MealItem.Nutrition.FatGrams,
                o.MealItem.Nutrition == null ? null : o.MealItem.Nutrition.FiberGrams,
                o.DisplayOrder,
                o.MealItem.Allergens
                    .Where(a => a.Allergen.IsActive)
                    .OrderBy(a => a.Allergen.Code)
                    .Select(a => new GuestCodeNameResponse(
                        a.Allergen.Code,
                        a.Allergen.Translations.Where(t => t.LanguageCode == language).Select(t => t.Name).FirstOrDefault()
                            ?? a.Allergen.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault()
                            ?? a.Allergen.Translations.Select(t => t.Name).FirstOrDefault()
                            ?? a.Allergen.Code))
                    .ToList()))
            .ToListAsync(ct);

        var meals = mealRows.Select(m => new GuestMealResponse(
            m.Id,
            m.Code,
            m.Name,
            m.Description,
            m.ImageUrl,
            m.ThumbnailUrl,
            new GuestMealTimeSummary(m.MealTimeCode, m.MealTimeName),
            new GuestNutritionResponse(m.Calories, m.Protein, m.Carbs, m.Fat, m.Fiber),
            [],
            m.Allergens,
            true,
            m.DisplayOrder)).ToArray();

        var response = new GuestHomeResponse(
            new GuestHeroResponse(selectedPlan.Name, selectedPlan.Description, selectedPlan.ImageUrl),
            plans.Select(p => new GuestPlanResponse(
                p.Id,
                p.Code,
                p.Name,
                p.Description,
                p.ImageUrl,
                null,
                p.DisplayOrder,
                p.Id == selectedPlan.Id)).ToArray(),
            weeklyCalendar,
            mealTypes,
            meals,
            new GuestPaginationResponse(
                request.Page,
                request.PageSize,
                totalRecords,
                totalPages,
                request.Page < totalPages,
                request.Page > 1));

        cache.Set(cacheKey, response, CacheDuration);
        return response;
    }

    private sealed record PlanRow(
        Guid Id,
        string Code,
        int DisplayOrder,
        string? ImageUrl,
        DateOnly? ValidFrom,
        DateOnly? ValidUntil,
        string Name,
        string Description);

    private sealed record MealRow(
        Guid Id,
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
