using System.Globalization;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace DietTime.Persistence;

public sealed class MealQueryService(DietTimeDbContext db, IStorageUrlService storage, IMemoryCache cache) : IMealQueryService
{
    public async Task<IReadOnlyList<PlanCategoryResponse>> GetPlanCategoriesAsync(string language, DateOnly today, CancellationToken ct)
    {
        var rows = await db.MealPlanTemplates.AsNoTracking()
            .Where(p => p.IsActive && p.IsPublished && !db.MealPlanTemplates.Any(v => v.VersionGroupId == p.VersionGroupId && v.IsPublished && v.VersionNumber > p.VersionNumber) && (p.ValidFrom == null || p.ValidFrom <= today) && (p.ValidUntil == null || p.ValidUntil >= today))
            .OrderBy(p => p.PlanType).ThenBy(p => p.Code)
            .Select(p => new
            {
                p.Id,
                p.Code,
                Name = p.Translations.Where(t => t.LanguageCode == language).Select(t => t.Name).FirstOrDefault() ?? p.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault() ?? p.Translations.Select(t => t.Name).FirstOrDefault() ?? p.Code,
                Description = p.Translations.Where(t => t.LanguageCode == language).Select(t => t.ShortDescription).FirstOrDefault() ?? p.Translations.Where(t => t.LanguageCode == "en").Select(t => t.ShortDescription).FirstOrDefault() ?? p.Translations.Select(t => t.ShortDescription).FirstOrDefault(),
                PlanMedia = db.MealMedia
                    .Where(m => m.EntityId == p.Id && m.Status == "ACTIVE" && m.MediaType == MealMediaTypes.MealPlan)
                    .OrderByDescending(m => m.IsPrimary)
                    .ThenBy(m => m.DisplayOrder)
                    .Select(m => new { m.PublicUrl, m.ObjectKey, m.ThumbnailUrl, m.ThumbnailObjectKey })
                    .FirstOrDefault()
            }).ToListAsync(ct);
        return rows.Select(x => new PlanCategoryResponse(
            x.Id,
            x.Code,
            x.Name,
            x.Description,
            x.PlanMedia is null
                ? null
                : Image(x.PlanMedia.ThumbnailUrl ?? x.PlanMedia.PublicUrl, x.PlanMedia.ThumbnailObjectKey ?? x.PlanMedia.ObjectKey, true),
            false)).ToArray();
    }

    public async Task<MealPlanResponse?> GetPlanAsync(Guid planId, string language, DateOnly today, CancellationToken ct)
    {
        var plan = await db.MealPlanTemplates.AsNoTracking().Where(p => p.Id == planId && p.IsActive && p.IsPublished && (p.ValidFrom == null || p.ValidFrom <= today) && (p.ValidUntil == null || p.ValidUntil >= today))
            .Select(p => new
            {
                p.Id,
                p.Code,
                p.PlanType,
                p.DurationDays,
                p.IsCustomizable,
                Name = p.Translations.Where(t => t.LanguageCode == language).Select(t => t.Name).FirstOrDefault() ?? p.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault() ?? p.Translations.Select(t => t.Name).FirstOrDefault() ?? p.Code,
                Description = p.Translations.Where(t => t.LanguageCode == language).Select(t => t.FullDescription ?? t.ShortDescription).FirstOrDefault() ?? p.Translations.Where(t => t.LanguageCode == "en").Select(t => t.FullDescription ?? t.ShortDescription).FirstOrDefault() ?? p.Translations.Select(t => t.FullDescription ?? t.ShortDescription).FirstOrDefault(),
                Prices = p.Prices.Where(x => x.IsActive).OrderBy(x => x.Amount).Select(x => new PlanPriceResponse(x.DurationDays, x.MealsPerDay, x.SnacksPerDay, x.Amount, x.CurrencyCode.Trim())).ToList(),
                Types = p.Days.SelectMany(d => d.Slots).Where(s => s.IsActive).Select(s => new { s.MealType.Id, s.MealType.Code, s.MealType.DisplayOrder, Name = s.MealType.Translations.Where(t => t.LanguageCode == language).Select(t => t.Name).FirstOrDefault() ?? s.MealType.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault() ?? s.MealType.Translations.Select(t => t.Name).FirstOrDefault() ?? s.MealType.Code }).Distinct().ToList()
            }).SingleOrDefaultAsync(ct);
        return plan is null ? null : new MealPlanResponse(plan.Id, plan.Code, plan.Name, plan.Description, plan.PlanType, plan.DurationDays, plan.IsCustomizable, plan.Prices, plan.Types.OrderBy(x => x.DisplayOrder).Select(x => new MealTypeResponse(x.Id, x.Code, x.Name, x.DisplayOrder)).ToArray());
    }

    public async Task<IReadOnlyList<CalendarDayResponse>?> GetCalendarAsync(Guid planId, DateOnly startDate, int numberOfDays, string language, CancellationToken ct)
    {
        var plan = await db.MealPlanTemplates.AsNoTracking().Where(p => p.Id == planId && p.IsActive && p.IsPublished)
            .Select(p => new { Days = p.Days.Where(d => d.IsActive).Select(d => new { d.Id, d.MenuWeekday }).ToList() }).SingleOrDefaultAsync(ct);
        if (plan is null) return null;
        var culture = CultureInfo.GetCultureInfo(language == "ar" ? "ar-QA" : "en-US");
        var result = new List<CalendarDayResponse>(numberOfDays);
        for (var i = 0; i < numberOfDays; i++)
        {
            var date = startDate.AddDays(i); var weekday = MenuWeekdayExtensions.FromDate(date);
            var day = plan.Days.FirstOrDefault(d => d.MenuWeekday == weekday);
            if (day is null) continue;
            result.Add(new CalendarDayResponse(day.Id, date, weekday, culture.DateTimeFormat.GetAbbreviatedDayName(date.DayOfWeek), culture.DateTimeFormat.GetDayName(date.DayOfWeek), true));
        }
        return result;
    }

    public async Task<PagedResult<MealCardResponse>?> GetPlanMealsAsync(Guid planId, MealListQuery request, string language, DateTimeOffset now, CancellationToken ct)
    {
        var dayId = request.TemplateDayId;
        if (dayId is null)
        {
            if (request.Date is null) return null;
            var plan = await db.MealPlanTemplates.AsNoTracking().Where(p => p.Id == planId && p.IsActive && p.IsPublished).Select(p => new { Days = p.Days.Where(d => d.IsActive).Select(d => new { d.Id, d.MenuWeekday }).ToList() }).SingleOrDefaultAsync(ct);
            if (plan is null) return null;
            var weekday = MenuWeekdayExtensions.FromDate(request.Date.Value);
            dayId = plan.Days.FirstOrDefault(d => d.MenuWeekday == weekday)?.Id;
        }
        if (dayId is null) return new PagedResult<MealCardResponse>([], new(request.Page, request.PageSize, 0, 0));

        var queryDate = request.Date ?? DateOnly.FromDateTime(now.UtcDateTime);
        var q = db.MealPlanSlotOptions.AsNoTracking().Where(o => o.Slot.MealPlanTemplateDayId == dayId && o.Slot.Day.MealPlanTemplateId == planId && o.Slot.IsActive && o.Slot.Day.IsActive && o.Slot.Day.Plan.IsActive && o.Slot.Day.Plan.IsPublished && (o.Slot.Day.Plan.ValidFrom == null || o.Slot.Day.Plan.ValidFrom <= queryDate) && (o.Slot.Day.Plan.ValidUntil == null || o.Slot.Day.Plan.ValidUntil >= queryDate) && o.IsAvailable && (o.AvailableFrom == null || o.AvailableFrom <= now) && (o.AvailableUntil == null || o.AvailableUntil > now) && o.MealItem.Status == "ACTIVE" && o.MealItem.IsAvailable && (o.MealItem.AvailableFrom == null || o.MealItem.AvailableFrom <= now) && (o.MealItem.AvailableUntil == null || o.MealItem.AvailableUntil > now));
        if (!string.IsNullOrWhiteSpace(request.MealType) && request.MealType != "ALL") q = q.Where(o => o.Slot.MealType.Code == request.MealType.ToUpper());
        if (request.CategoryId.HasValue) q = q.Where(o => o.MealItem.CategoryId == request.CategoryId);
        if (!string.IsNullOrWhiteSpace(request.Search)) { var term = request.Search.Trim(); q = q.Where(o => o.MealItem.Translations.Any(t => t.LanguageCode == language && EF.Functions.ILike(t.Name, $"%{term}%"))); }
        var count = await q.CountAsync(ct); var pages = (int)Math.Ceiling(count / (double)request.PageSize);
        var rows = await q.OrderBy(o => o.Slot.DisplayOrder).ThenBy(o => o.DisplayOrder).Skip((request.Page - 1) * request.PageSize).Take(request.PageSize)
            .Select(o => new CardRow(o.Id, o.MealPlanTemplateSlotId, o.MealItemId, o.Slot.MealType.Id, o.Slot.MealType.Code, o.Slot.MealType.DisplayOrder,
                o.Slot.MealType.Translations.Where(t => t.LanguageCode == language).Select(t => t.Name).FirstOrDefault() ?? o.Slot.MealType.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault() ?? o.Slot.MealType.Code,
                o.MealItem.Translations.Where(t => t.LanguageCode == language).Select(t => t.Name).FirstOrDefault() ?? o.MealItem.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault() ?? o.MealItem.Sku,
                o.MealItem.Translations.Where(t => t.LanguageCode == language).Select(t => t.ShortDescription).FirstOrDefault() ?? o.MealItem.Translations.Where(t => t.LanguageCode == "en").Select(t => t.ShortDescription).FirstOrDefault(),
                db.MealMedia.Where(m => m.EntityId == o.MealItemId && m.MediaType == MealMediaTypes.MealItem && m.Status == "ACTIVE" && m.IsPrimary).Select(m => m.ThumbnailUrl ?? m.PublicUrl).FirstOrDefault(), db.MealMedia.Where(m => m.EntityId == o.MealItemId && m.MediaType == MealMediaTypes.MealItem && m.Status == "ACTIVE" && m.IsPrimary).Select(m => m.ThumbnailObjectKey ?? m.ObjectKey).FirstOrDefault(),
                o.MealItem.Nutrition == null ? null : o.MealItem.Nutrition.CaloriesKcal, o.MealItem.Nutrition == null ? null : o.MealItem.Nutrition.ProteinGrams, o.MealItem.Nutrition == null ? null : o.MealItem.Nutrition.CarbohydratesGrams, o.MealItem.Nutrition == null ? null : o.MealItem.Nutrition.FatGrams, o.AdditionalPrice, o.IsDefault)).ToListAsync(ct);
        var ids = rows.Select(x => x.MealItemId).ToArray();
        var allergens = await db.MealItemAllergens.AsNoTracking().Where(x => ids.Contains(x.MealItemId)).Select(x => new { x.MealItemId, x.Allergen.Code }).ToListAsync(ct);
        var cards = rows.Select(x => new MealCardResponse(x.Id, x.SlotId, x.MealItemId, new(x.MealTypeId, x.MealTypeCode, x.MealTypeName, x.MealTypeOrder), x.Name, x.Description, Image(x.ImageUrl, x.ObjectKey, true), x.Calories, x.Protein, x.Carbs, x.Fat, x.AdditionalPrice, "QAR", x.IsDefault, true, allergens.Where(a => a.MealItemId == x.MealItemId).Select(a => a.Code).ToArray())).ToArray();
        return new(cards, new(request.Page, request.PageSize, count, pages));
    }

    public async Task<MealDetailsResponse?> GetMealAsync(Guid mealId, string language, DateTimeOffset now, CancellationToken ct)
    {
        var x = await db.MealItems.AsNoTracking().Where(m => m.Id == mealId && m.Status == "ACTIVE" && m.IsAvailable && (m.AvailableFrom == null || m.AvailableFrom <= now) && (m.AvailableUntil == null || m.AvailableUntil > now))
            .Select(m => new
            {
                m.Id,
                m.Sku,
                m.PreparationTimeMinutes,
                m.IsVegetarian,
                m.IsVegan,
                m.IsGlutenFree,
                m.IsDairyFree,
                m.IsAvailable,
                Name = m.Translations.Where(t => t.LanguageCode == language).Select(t => t.Name).FirstOrDefault() ?? m.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault() ?? m.Sku,
                Short = m.Translations.Where(t => t.LanguageCode == language).Select(t => t.ShortDescription).FirstOrDefault() ?? m.Translations.Where(t => t.LanguageCode == "en").Select(t => t.ShortDescription).FirstOrDefault(),
                Full = m.Translations.Where(t => t.LanguageCode == language).Select(t => t.FullDescription).FirstOrDefault() ?? m.Translations.Where(t => t.LanguageCode == "en").Select(t => t.FullDescription).FirstOrDefault(),
                Category = new { m.Category.Id, m.Category.Code, Name = m.Category.Translations.Where(t => t.LanguageCode == language).Select(t => t.Name).FirstOrDefault() ?? m.Category.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault() ?? m.Category.Code },
                Nutrition = m.Nutrition == null ? null : new NutritionResponse(m.Nutrition.ServingQuantity, m.Nutrition.ServingUnit, m.Nutrition.CaloriesKcal, m.Nutrition.ProteinGrams, m.Nutrition.CarbohydratesGrams, m.Nutrition.FatGrams, m.Nutrition.SaturatedFatGrams,m.Nutrition.TransFatGrams, m.Nutrition.FiberGrams, m.Nutrition.SugarGrams, m.Nutrition.SodiumMg, m.Nutrition.CholesterolMg),
                Media = db.MealMedia.Where(v => v.EntityId == m.Id && v.Status == "ACTIVE" && v.MediaType == MealMediaTypes.MealItem).OrderBy(v => v.DisplayOrder).Select(v => new { v.Id, v.PublicUrl, v.ObjectKey, v.ThumbnailUrl, v.ThumbnailObjectKey, v.IsPrimary, Alt = v.Translations.Where(t => t.LanguageCode == language).Select(t => t.AltText).FirstOrDefault() ?? v.Translations.Where(t => t.LanguageCode == "en").Select(t => t.AltText).FirstOrDefault() }).ToList(),
                Ingredients = m.Ingredients.OrderBy(i => i.DisplayOrder).Select(i => new IngredientResponse(i.IngredientId, i.Ingredient.Translations.Where(t => t.LanguageCode == language).Select(t => t.Name).FirstOrDefault() ?? i.Ingredient.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault() ?? i.Ingredient.Code, i.Quantity, i.Unit, i.IsOptional, i.CanBeRemoved)).ToList(),
                Allergens = m.Allergens.Select(a => new AllergenResponse(a.AllergenId, a.Allergen.Code, a.Allergen.Translations.Where(t => t.LanguageCode == language).Select(t => t.Name).FirstOrDefault() ?? a.Allergen.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault() ?? a.Allergen.Code, a.AllergenLevel)).ToList(),
                Price = m.Prices.Where(p => p.IsActive && p.PriceType == "INDIVIDUAL" && p.EffectiveFrom <= now && (p.EffectiveUntil == null || p.EffectiveUntil > now)).OrderByDescending(p => p.EffectiveFrom).Select(p => new MoneyResponse(p.Amount, p.CurrencyCode.Trim())).FirstOrDefault()
            }).SingleOrDefaultAsync(ct);
        if (x is null) return null;
        var media = x.Media.Select(v => new MediaResponse(v.Id, Image(v.PublicUrl, v.ObjectKey, false)!, Image(v.ThumbnailUrl, v.ThumbnailObjectKey, true), v.Alt)).ToArray();
        var primary = x.Media.FirstOrDefault(v => v.IsPrimary);
        return new(x.Id, x.Sku, x.Name, x.Short, x.Full, new(x.Category.Id, x.Category.Code, x.Category.Name), primary is null ? media.FirstOrDefault()?.ImageUrl : Image(primary.PublicUrl, primary.ObjectKey, false), media, x.Nutrition, x.Ingredients, x.Allergens, x.Price, x.PreparationTimeMinutes, x.IsVegetarian, x.IsVegan, x.IsGlutenFree, x.IsDairyFree, x.IsAvailable);
    }

    public async Task<IReadOnlyList<MealTypeResponse>> GetMealTypesAsync(string language, CancellationToken ct)
    {
        var cacheKey = $"meal-types:{language}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<MealTypeResponse>? cached) && cached is not null)
            return cached;
        var rows = await db.MealTypes.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.DisplayOrder).Select(x => new MealTypeResponse(x.Id, x.Code, x.Translations.Where(t => t.LanguageCode == language).Select(t => t.Name).FirstOrDefault() ?? x.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault() ?? x.Code, x.DisplayOrder)).ToListAsync(ct);
        rows.Insert(0, new(null, "ALL", language == "ar" ? "الكل" : "All", 0));
        cache.Set(cacheKey, rows, TimeSpan.FromMinutes(15));
        return rows;
    }

    public async Task<PagedResult<MealSearchResponse>> SearchMealsAsync(MealSearchQuery request, string language, DateTimeOffset now, CancellationToken ct)
    {
        var q = db.MealItems.AsNoTracking().Where(m => m.Status == "ACTIVE" && !db.MealItems.Any(v => v.VersionGroupId == m.VersionGroupId && v.Status == "ACTIVE" && v.VersionNumber > m.VersionNumber) && !db.MealItems.Any(v => v.VersionGroupId == m.VersionGroupId && v.IsLatest && (v.Status == "INACTIVE" || v.Status == "ARCHIVED")) && m.IsAvailable && (m.AvailableFrom == null || m.AvailableFrom <= now) && (m.AvailableUntil == null || m.AvailableUntil > now));
        if (request.CategoryId.HasValue) q = q.Where(m => m.CategoryId == request.CategoryId); if (request.IsVegetarian.HasValue) q = q.Where(m => m.IsVegetarian == request.IsVegetarian); if (request.IsVegan.HasValue) q = q.Where(m => m.IsVegan == request.IsVegan); if (request.IsGlutenFree.HasValue) q = q.Where(m => m.IsGlutenFree == request.IsGlutenFree);
        if (request.MinimumProtein.HasValue) q = q.Where(m => m.Nutrition != null && m.Nutrition.ProteinGrams >= request.MinimumProtein); if (request.MaximumCalories.HasValue) q = q.Where(m => m.Nutrition != null && m.Nutrition.CaloriesKcal <= request.MaximumCalories);
        if (!string.IsNullOrWhiteSpace(request.MealType)) q = q.Where(m => db.MealPlanSlotOptions.Any(o => o.MealItemId == m.Id && o.Slot.MealType.Code == request.MealType.ToUpper()));
        if (!string.IsNullOrWhiteSpace(request.Search)) { var term = request.Search.Trim(); q = q.Where(m => m.Translations.Any(t => t.LanguageCode == language && EF.Functions.ILike(t.Name, $"%{term}%"))); }
        var count = await q.CountAsync(ct); var rows = await q.OrderBy(m => m.Sku).Skip((request.Page - 1) * request.PageSize).Take(request.PageSize).Select(m => new { m.Id, m.Sku, Name = m.Translations.Where(t => t.LanguageCode == language).Select(t => t.Name).FirstOrDefault() ?? m.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault() ?? m.Sku, Description = m.Translations.Where(t => t.LanguageCode == language).Select(t => t.ShortDescription).FirstOrDefault() ?? m.Translations.Where(t => t.LanguageCode == "en").Select(t => t.ShortDescription).FirstOrDefault(), Url = db.MealMedia.Where(x => x.EntityId == m.Id && x.MediaType == MealMediaTypes.MealItem && x.IsPrimary && x.Status == "ACTIVE").Select(x => x.ThumbnailUrl ?? x.PublicUrl).FirstOrDefault(), Key = db.MealMedia.Where(x => x.EntityId == m.Id && x.MediaType == MealMediaTypes.MealItem && x.IsPrimary && x.Status == "ACTIVE").Select(x => x.ThumbnailObjectKey ?? x.ObjectKey).FirstOrDefault(), Calories = m.Nutrition == null ? null : m.Nutrition.CaloriesKcal, Protein = m.Nutrition == null ? null : m.Nutrition.ProteinGrams, Carbs = m.Nutrition == null ? null : m.Nutrition.CarbohydratesGrams, Fat = m.Nutrition == null ? null : m.Nutrition.FatGrams, Price = m.Prices.Where(p => p.IsActive && p.PriceType == "INDIVIDUAL" && p.EffectiveFrom <= now && (p.EffectiveUntil == null || p.EffectiveUntil > now)).OrderByDescending(p => p.EffectiveFrom).Select(p => (decimal?)p.Amount).FirstOrDefault(), Currency = m.Prices.Where(p => p.IsActive && p.PriceType == "INDIVIDUAL").Select(p => p.CurrencyCode).FirstOrDefault() }).ToListAsync(ct);
        return new(rows.Select(m => new MealSearchResponse(m.Id, m.Sku, m.Name, m.Description, Image(m.Url, m.Key, true), m.Calories, m.Protein, m.Carbs, m.Fat, m.Price, m.Currency?.Trim(), true)).ToArray(), new(request.Page, request.PageSize, count, (int)Math.Ceiling(count / (double)request.PageSize)));
    }

    private string? Image(string? explicitUrl, string? objectKey, bool thumbnail) => !string.IsNullOrWhiteSpace(explicitUrl) ? explicitUrl : string.IsNullOrWhiteSpace(objectKey) ? null : thumbnail ? storage.GetThumbnailUrl(objectKey) : storage.GetPublicUrl(objectKey);
    private sealed record CardRow(Guid Id, Guid SlotId, Guid MealItemId, Guid MealTypeId, string MealTypeCode, int MealTypeOrder, string MealTypeName, string Name, string? Description, string? ImageUrl, string? ObjectKey, decimal? Calories, decimal? Protein, decimal? Carbs, decimal? Fat, decimal AdditionalPrice, bool IsDefault);
}
