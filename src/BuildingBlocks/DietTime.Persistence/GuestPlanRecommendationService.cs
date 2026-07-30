using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Persistence;

public sealed class GuestPlanRecommendationService(
    DietTimeDbContext db,
    IStorageUrlService storage,
    TimeProvider clock) : IGuestPlanRecommendationService
{
    public async Task<IReadOnlyList<GuestPlanRecommendationResponse>> GetAsync(
        Guid profileId,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var profile = await db.CustomerProfiles.AsNoTracking()
            .Include(x => x.Preferences)
            .Include(x => x.Allergens)
                .ThenInclude(x => x.Allergen)
            .Include(x => x.NutritionTargets.Where(target => target.IsCurrent))
            .AsSplitQuery()
            .SingleOrDefaultAsync(x =>
                x.Id == profileId &&
                x.UserId == null &&
                x.IsActive &&
                x.GuestTokenExpiresAt > now,
                ct)
            ?? throw new InvalidGuestSessionException();

        var plans = await db.MealPlanTemplates.AsNoTracking()
            .Where(plan =>
                plan.IsActive &&
                plan.IsPublished &&
                (plan.ValidFrom == null || plan.ValidFrom <= today) &&
                (plan.ValidUntil == null || plan.ValidUntil >= today) &&
                !db.MealPlanTemplates.Any(version =>
                    version.VersionGroupId == plan.VersionGroupId &&
                    version.IsPublished &&
                    version.VersionNumber > plan.VersionNumber))
            .Include(x => x.Translations)
            .Include(x => x.Days.Where(day => day.IsActive))
                .ThenInclude(x => x.Slots.Where(slot => slot.IsActive))
                    .ThenInclude(x => x.Options.Where(option => option.IsAvailable))
                        .ThenInclude(x => x.MealItem)
                            .ThenInclude(x => x.Allergens)
                                .ThenInclude(x => x.Allergen)
            .AsSplitQuery()
            .ToListAsync(ct);

        var planIds = plans.Select(x => x.Id).ToArray();
        var images = await db.MealMedia.AsNoTracking()
            .Where(media =>
                planIds.Contains(media.EntityId) &&
                media.MediaType == MealMediaTypes.MealPlan &&
                media.Status == "ACTIVE" &&
                media.IsPrimary)
            .OrderBy(media => media.DisplayOrder)
            .ToDictionaryAsync(media => media.EntityId, ct);

        var confirmedAllergens = profile.Allergens
            .Where(x => x.MedicallyConfirmed)
            .Select(x => x.Allergen.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reportedAllergens = profile.Allergens
            .Select(x => x.Allergen.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var recommendations = new List<GuestPlanRecommendationResponse>();
        foreach (var plan in plans)
        {
            var planAllergens = plan.Days
                .SelectMany(day => day.Slots)
                .SelectMany(slot => slot.Options)
                .Where(option =>
                    option.MealItem.Status == "ACTIVE" &&
                    option.MealItem.IsAvailable &&
                    (option.MealItem.AvailableFrom == null || option.MealItem.AvailableFrom <= now) &&
                    (option.MealItem.AvailableUntil == null || option.MealItem.AvailableUntil > now))
                .SelectMany(option => option.MealItem.Allergens)
                .Where(link => link.Allergen.IsActive)
                .Select(link => link.Allergen.Code)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (planAllergens.Overlaps(confirmedAllergens))
                continue;

            var warnings = planAllergens
                .Where(reportedAllergens.Contains)
                .Order()
                .Select(code => $"Some meal choices contain {code}.")
                .ToArray();
            var goalCompatible = string.IsNullOrWhiteSpace(profile.GoalCode) || plan.IsCustomizable;
            var activityCompatible =
                string.IsNullOrWhiteSpace(profile.ActivityLevelCode) ||
                profile.NutritionTargets.Any();
            var reasons = new List<string>();
            var score = 50m;
            if (goalCompatible && !string.IsNullOrWhiteSpace(profile.GoalCode))
            {
                score += 20;
                reasons.Add($"Customizable meal choices can support the {profile.GoalCode} goal.");
            }
            if (activityCompatible && !string.IsNullOrWhiteSpace(profile.ActivityLevelCode))
            {
                score += 15;
                reasons.Add("Meal choices can be compared with the calculated nutrition target.");
            }
            if (profile.Preferences.Count > 0 && plan.IsCustomizable)
            {
                score += Math.Min(10, profile.Preferences.Count * 2);
                reasons.Add("The plan supports preference-based meal customization.");
            }
            if (warnings.Length == 0)
            {
                score += 15;
                reasons.Add("No reported allergen conflicts were found.");
            }
            else
            {
                score -= warnings.Length * 10;
            }

            images.TryGetValue(plan.Id, out var image);
            recommendations.Add(new(
                plan.Id,
                plan.Code,
                Localized(plan.Translations, profile.PreferredLanguage, x => x.Name) ?? plan.Code,
                Localized(plan.Translations, profile.PreferredLanguage, x => x.ShortDescription),
                image is null
                    ? null
                    : image.PublicUrl ?? storage.GetPublicUrl(image.ObjectKey),
                Math.Clamp(score, 0m, 100m),
                reasons,
                goalCompatible,
                activityCompatible,
                warnings.Length > 0,
                warnings));
        }

        return recommendations
            .OrderByDescending(x => x.RecommendationScore)
            .ThenBy(x => x.PlanCode)
            .ToArray();
    }

    private static string? Localized(
        IEnumerable<MealPlanTemplateTranslation> translations,
        string language,
        Func<MealPlanTemplateTranslation, string?> selector)
    {
        var rows = translations.ToArray();
        var row = rows.FirstOrDefault(x =>
                x.LanguageCode.Equals(language, StringComparison.OrdinalIgnoreCase))
            ?? rows.FirstOrDefault(x =>
                x.LanguageCode.Equals("en", StringComparison.OrdinalIgnoreCase))
            ?? rows.FirstOrDefault();
        return row is null ? null : selector(row);
    }
}
