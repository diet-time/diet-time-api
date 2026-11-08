using DietTime.Domain;

namespace DietTime.Application;

/// <summary>Prepares independent prices for a published menu without changing historical purchases.</summary>
public static class PlanPricingPublication
{
    public static IReadOnlyList<MealPlanPrice> Prepare(
        MealPlanTemplate source, MealPlanTemplate target,
        IReadOnlyCollection<MealPlanPricePackage> durations, DateTimeOffset now, Guid? userId)
    {
        if (!target.IsPublished || !source.IsPublished || source.Id == target.Id) return [];
        if (source.VersionGroupId != target.VersionGroupId)
            throw new InvalidOperationException("Prices can only be carried forward within the same meal plan.");

        var copies = new List<MealPlanPrice>();
        foreach (var price in source.Prices.Where(price => price.IsActive &&
                     (price.EffectiveUntil == null || price.EffectiveUntil > now)))
        {
            // A price deliberately configured on the draft (including an inactive one) wins.
            if (target.Prices.Any(existing => existing.PackageCode == price.PackageCode &&
                    existing.PackageOptionId == price.PackageOptionId)) continue;

            var duration = durations.SingleOrDefault(item => item.Code == price.PackageCode);
            var package = price.PackageOption;
            if (source.PlanType != target.PlanType || source.DurationDays != target.DurationDays ||
                source.IsCustomizable != target.IsCustomizable ||
                duration is null || !duration.IsActive || duration.DurationDays != price.DurationDays ||
                package is null || !package.IsActive || package.MealCount != price.MealsPerDay ||
                package.SnackCount != price.SnacksPerDay)
                throw new TemplateDayException("PRICING_REVIEW_REQUIRED",
                    $"Save a draft and review its Plan Pricing before publishing. Price {price.Id} " +
                    $"({price.PackageCode ?? "missing duration"}) cannot be carried forward because the plan, package or duration has changed. " +
                    "Deactivate outdated pricing, or configure a reviewed replacement for the same package and duration on the saved draft.", 422);

            var copy = new MealPlanPrice
            {
                Id = Guid.NewGuid(), MealPlanTemplateId = target.Id,
                PackageCode = price.PackageCode, PackageOptionId = price.PackageOptionId,
                DurationDays = price.DurationDays, MealsPerDay = price.MealsPerDay, SnacksPerDay = price.SnacksPerDay,
                CurrencyCode = price.CurrencyCode, Amount = price.Amount, DiscountedPrice = price.DiscountedPrice,
                EffectiveFrom = price.EffectiveFrom, EffectiveUntil = price.EffectiveUntil, IsActive = price.IsActive,
                CreatedAt = now, UpdatedAt = now, CreatedBy = userId, UpdatedBy = userId,
                Translations = price.Translations.Select(translation => new MealPlanPriceTranslation
                {
                    Id = Guid.NewGuid(), LanguageCode = translation.LanguageCode,
                    Name = translation.Name, Description = translation.Description, CreatedAt = now, UpdatedAt = now
                }).ToList()
            };
            copies.Add(copy);
        }
        return copies;
    }
}
