using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Persistence;

public sealed class CustomerMealPlanPurchaseService(
    DietTimeDbContext db,
    IStorageUrlService storage,
    TimeProvider clock) : ICustomerMealPlanPurchaseService
{
    public async Task<MealPlanPurchaseOptionsResponse?> GetPurchaseOptionsAsync(
        string mealPlanCodeOrId,
        Guid userId,
        string language,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var normalizedCode = mealPlanCodeOrId.Trim().ToUpperInvariant();
        var isId = Guid.TryParse(mealPlanCodeOrId, out var requestedId);

        var plans = db.MealPlanTemplates.AsNoTracking()
            .Where(plan =>
                plan.IsActive &&
                plan.IsPublished &&
                (plan.ValidFrom == null || plan.ValidFrom <= today) &&
                (plan.ValidUntil == null || plan.ValidUntil >= today));

        plans = isId
            ? plans.Where(plan => plan.Id == requestedId)
            : plans.Where(plan =>
                plan.Code == normalizedCode &&
                !db.MealPlanTemplates.Any(version =>
                    version.VersionGroupId == plan.VersionGroupId &&
                    version.IsPublished &&
                    version.VersionNumber > plan.VersionNumber));

        var plan = await plans
            .Select(plan => new
            {
                plan.Id,
                plan.VersionGroupId,
                plan.Code,
                Name = plan.Translations
                    .Where(translation => translation.LanguageCode == language)
                    .Select(translation => translation.Name)
                    .FirstOrDefault()
                    ?? plan.Translations
                        .Where(translation => translation.LanguageCode == "en")
                        .Select(translation => translation.Name)
                        .FirstOrDefault()
                    ?? plan.Translations.Select(translation => translation.Name).FirstOrDefault()
                    ?? plan.Code,
                ShortDescription = plan.Translations
                    .Where(translation => translation.LanguageCode == language)
                    .Select(translation => translation.ShortDescription)
                    .FirstOrDefault()
                    ?? plan.Translations
                        .Where(translation => translation.LanguageCode == "en")
                        .Select(translation => translation.ShortDescription)
                        .FirstOrDefault()
                    ?? plan.Translations.Select(translation => translation.ShortDescription).FirstOrDefault()
            })
            .SingleOrDefaultAsync(ct);

        if (plan is null)
            return null;

        var media = await db.MealMedia.AsNoTracking()
            .Where(item =>
                item.Status == "ACTIVE" &&
                item.MediaType == MealMediaTypes.MealPlan &&
                db.MealPlanTemplates.Any(mediaPlan =>
                    mediaPlan.Id == item.EntityId &&
                    mediaPlan.VersionGroupId == plan.VersionGroupId))
            .OrderByDescending(item => item.EntityId == plan.Id)
            .ThenByDescending(item => item.IsPrimary)
            .ThenBy(item => item.DisplayOrder)
            .ThenByDescending(item => item.UpdatedAt)
            .Select(item => new
            {
                item.ThumbnailUrl,
                item.ThumbnailObjectKey,
                item.PublicUrl,
                item.ObjectKey
            })
            .FirstOrDefaultAsync(ct);

        var priceRows = await (
            from price in db.MealPlanPrices.AsNoTracking()
            join package in db.MealPlanPricePackages.AsNoTracking()
                on price.DurationDays equals package.DurationDays
            where price.MealPlanTemplateId == plan.Id
                && price.IsActive
                && price.EffectiveFrom <= now
                && (price.EffectiveUntil == null || price.EffectiveUntil > now)
                && package.IsActive
            orderby price.MealsPerDay, price.SnacksPerDay, package.DisplayOrder, price.Amount
            select new PriceRow(
                price.Id,
                price.MealsPerDay,
                price.SnacksPerDay,
                price.CurrencyCode,
                price.Amount,
                package.Code,
                language == "ar" ? package.NameAr : package.NameEn,
                package.DurationDays,
                package.DisplayOrder,
                price.Translations
                    .Where(translation => translation.LanguageCode == language)
                    .Select(translation => translation.Name)
                    .FirstOrDefault()
                    ?? price.Translations
                        .Where(translation => translation.LanguageCode == "en")
                        .Select(translation => translation.Name)
                        .FirstOrDefault(),
                price.Translations
                    .Where(translation => translation.LanguageCode == language)
                    .Select(translation => translation.Description)
                    .FirstOrDefault()
                    ?? price.Translations
                        .Where(translation => translation.LanguageCode == "en")
                        .Select(translation => translation.Description)
                        .FirstOrDefault()))
            .ToListAsync(ct);

        var calorieRows = await db.MealPlanSlotOptions.AsNoTracking()
            .Where(option =>
                option.Slot.Day.MealPlanTemplateId == plan.Id &&
                option.Slot.Day.IsActive &&
                option.Slot.IsActive &&
                option.IsAvailable &&
                (option.AvailableFrom == null || option.AvailableFrom <= now) &&
                (option.AvailableUntil == null || option.AvailableUntil > now) &&
                option.MealItem.Status == "ACTIVE" &&
                option.MealItem.IsAvailable &&
                (option.MealItem.AvailableFrom == null || option.MealItem.AvailableFrom <= now) &&
                (option.MealItem.AvailableUntil == null || option.MealItem.AvailableUntil > now) &&
                option.MealItem.Nutrition != null &&
                option.MealItem.Nutrition.CaloriesKcal != null)
            .Select(option => new
            {
                DayId = option.Slot.MealPlanTemplateDayId,
                SlotId = option.MealPlanTemplateSlotId,
                option.IsDefault,
                option.DisplayOrder,
                CaloriesKcal = option.MealItem.Nutrition!.CaloriesKcal!.Value
            })
            .ToListAsync(ct);

        var estimatedCalories = calorieRows.Count == 0
            ? (decimal?)null
            : decimal.Round(
                calorieRows
                    .GroupBy(row => row.DayId)
                    .Average(day => day
                        .GroupBy(row => row.SlotId)
                        .Sum(slot => slot
                            .OrderByDescending(row => row.IsDefault)
                            .ThenBy(row => row.DisplayOrder)
                            .First()
                            .CaloriesKcal)),
                0,
                MidpointRounding.AwayFromZero);

        var hasRecordedAllergens = await db.CustomerProfileAllergens.AsNoTracking()
            .AnyAsync(item => item.CustomerProfile.UserId == userId, ct);

        var configurations = priceRows
            .GroupBy(row => new { row.MealsPerDay, row.SnacksPerDay })
            .OrderBy(group => group.Key.MealsPerDay)
            .ThenBy(group => group.Key.SnacksPerDay)
            .Select(group => new MealPlanMealConfigurationResponse(
                group.Key.MealsPerDay,
                group.Key.SnacksPerDay,
                ConfigurationName(group.Key.MealsPerDay, group.Key.SnacksPerDay, language),
                IncludedText(group.Key.MealsPerDay, group.Key.SnacksPerDay, language),
                group
                    .OrderBy(row => row.PackageDisplayOrder)
                    .ThenBy(row => row.Amount)
                    .Select(row => new MealPlanPurchasePackageResponse(
                        row.PriceId,
                        row.PackageCode,
                        row.PackageCode,
                        row.PackageName,
                        row.ServiceDays,
                        row.PackageDisplayOrder,
                        row.CurrencyCode.Trim(),
                        row.Amount,
                        DailyPrice(row.Amount, row.ServiceDays),
                        row.Name,
                        row.Description))
                    .ToArray()))
            .ToArray();

        return new MealPlanPurchaseOptionsResponse(
            new MealPlanPurchasePlanResponse(
                plan.Id,
                plan.Code,
                plan.Name,
                plan.ShortDescription,
                ResolveImage(media?.ThumbnailUrl, media?.ThumbnailObjectKey, media?.PublicUrl, media?.ObjectKey),
                estimatedCalories),
            configurations,
            hasRecordedAllergens);
    }

    public async Task<MealPlanSelectionValidationResult> ValidateSelectionAsync(
        ValidateMealPlanSelectionRequest request,
        CancellationToken ct)
    {
        var price = await db.MealPlanPrices.AsNoTracking()
            .Where(item => item.Id == request.MealPlanPriceId)
            .Select(item => new
            {
                item.Id,
                item.MealPlanTemplateId,
                item.DurationDays,
                item.MealsPerDay,
                item.SnacksPerDay,
                item.CurrencyCode,
                item.Amount,
                item.EffectiveFrom,
                item.EffectiveUntil,
                item.IsActive
            })
            .SingleOrDefaultAsync(ct);

        if (price is null)
            return new(MealPlanSelectionValidationStatus.PriceNotFound);
        if (price.MealPlanTemplateId != request.MealPlanTemplateId)
            return new(MealPlanSelectionValidationStatus.WrongPlan);
        if (!price.IsActive)
            return new(MealPlanSelectionValidationStatus.PriceInactive);

        var now = clock.GetUtcNow();
        if (price.EffectiveFrom > now)
            return new(MealPlanSelectionValidationStatus.PriceNotEffective);
        if (price.EffectiveUntil is not null && price.EffectiveUntil <= now)
            return new(MealPlanSelectionValidationStatus.PriceExpired);

        var package = await db.MealPlanPricePackages.AsNoTracking()
            .Where(item => item.DurationDays == price.DurationDays)
            .OrderBy(item => item.DisplayOrder)
            .Select(item => new { item.DurationDays, item.IsActive })
            .FirstOrDefaultAsync(ct);

        if (package is null)
            return new(MealPlanSelectionValidationStatus.PricePackageNotFound);
        if (!package.IsActive)
            return new(MealPlanSelectionValidationStatus.PricePackageInactive);

        return new(
            MealPlanSelectionValidationStatus.Valid,
            new MealPlanSelectionValidationResponse(
                true,
                price.MealPlanTemplateId,
                price.Id,
                price.MealsPerDay,
                price.SnacksPerDay,
                package.DurationDays,
                price.CurrencyCode.Trim(),
                price.Amount,
                DailyPrice(price.Amount, package.DurationDays)));
    }

    private string? ResolveImage(
        string? thumbnailUrl,
        string? thumbnailObjectKey,
        string? publicUrl,
        string? objectKey)
    {
        if (!string.IsNullOrWhiteSpace(thumbnailUrl)) return thumbnailUrl;
        if (!string.IsNullOrWhiteSpace(thumbnailObjectKey)) return storage.GetThumbnailUrl(thumbnailObjectKey);
        if (!string.IsNullOrWhiteSpace(publicUrl)) return publicUrl;
        return string.IsNullOrWhiteSpace(objectKey) ? null : storage.GetPublicUrl(objectKey);
    }

    private static decimal DailyPrice(decimal amount, int serviceDays) =>
        decimal.Round(amount / serviceDays, 2, MidpointRounding.AwayFromZero);

    private static string ConfigurationName(int meals, int snacks, string language) =>
        language == "ar"
            ? snacks == 0
                ? $"{meals} وجبات"
                : $"{meals} وجبات + {snacks} وجبات خفيفة"
            : snacks == 0
                ? $"{meals} {(meals == 1 ? "Meal" : "Meals")}"
                : $"{meals} {(meals == 1 ? "Meal" : "Meals")} + {snacks} {(snacks == 1 ? "Snack" : "Snacks")}";

    private static string IncludedText(int meals, int snacks, string language) =>
        language == "ar"
            ? snacks == 0
                ? $"{meals} وجبات لكل يوم خدمة"
                : $"{meals} وجبات و{snacks} وجبات خفيفة لكل يوم خدمة"
            : snacks == 0
                ? $"{meals} {(meals == 1 ? "meal" : "meals")} per service day"
                : $"{meals} {(meals == 1 ? "meal" : "meals")} and {snacks} {(snacks == 1 ? "snack" : "snacks")} per service day";

    private sealed record PriceRow(
        Guid PriceId,
        int MealsPerDay,
        int SnacksPerDay,
        string CurrencyCode,
        decimal Amount,
        string PackageCode,
        string PackageName,
        int ServiceDays,
        int PackageDisplayOrder,
        string? Name,
        string? Description);
}
