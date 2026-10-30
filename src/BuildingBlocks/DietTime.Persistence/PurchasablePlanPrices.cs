using DietTime.Domain;

namespace DietTime.Persistence;

/// <summary>One availability rule for plan cards, purchase options and order validation.</summary>
public static class PurchasablePlanPrices
{
    public static IQueryable<MealPlanPrice> Query(DietTimeDbContext db, DateTimeOffset now) =>
        Available(db.MealPlanPrices, db.MealPlanPricePackages, db.MealPlanTemplates, now);

    public static IQueryable<MealPlanPrice> Available(
        IQueryable<MealPlanPrice> prices,
        IQueryable<MealPlanPricePackage> durations,
        IQueryable<MealPlanTemplate> plans,
        DateTimeOffset now)
    {
        var today = DietTime.Application.SubscriptionMenuRules.QatarToday(now);
        return prices.Where(price =>
            price.IsActive && price.Amount > 0 && price.DurationDays > 0 &&
            price.CurrencyCode.Trim().Length == 3 &&
            price.EffectiveFrom <= now && (price.EffectiveUntil == null || price.EffectiveUntil > now) &&
            price.Plan.IsActive && price.Plan.IsPublished &&
            (price.Plan.ValidFrom == null || price.Plan.ValidFrom <= today) &&
            (price.Plan.ValidUntil == null || price.Plan.ValidUntil >= today) &&
            !plans.Any(version => version.VersionGroupId == price.Plan.VersionGroupId &&
                version.IsPublished && version.VersionNumber > price.Plan.VersionNumber) &&
            price.PackageOption != null && price.PackageOption.IsActive &&
            price.PackageOption.MealCount == price.MealsPerDay &&
            price.PackageOption.SnackCount == price.SnacksPerDay &&
            durations.Any(duration => duration.Code == price.PackageCode && duration.IsActive &&
                duration.DurationDays == price.DurationDays));
    }
}
