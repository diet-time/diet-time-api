using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DietTime.Persistence;

public sealed class CustomerProfileService(
    DietTimeDbContext db,
    ICustomerNutritionCalculator nutritionCalculator,
    TimeProvider clock,
    ILogger<CustomerProfileService> logger) : ICustomerProfileService
{
    public async Task<CustomerProfileResponse?> GetAsync(
        Guid userId,
        CancellationToken ct)
    {
        var profile = await ProfileQuery(tracking: false)
            .SingleOrDefaultAsync(x => x.UserId == userId, ct);
        return profile is null ? null : ToResponse(profile, Today());
    }

    public async Task<CustomerProfileUpsertResult> UpsertAsync(
        Guid userId,
        UpsertCustomerProfileRequest request,
        CancellationToken ct)
    {
        var requestedAllergenIds = request.Allergens
            .Select(x => x.AllergenId)
            .Distinct()
            .ToArray();
        var activeAllergens = await db.Allergens
            .Include(x => x.Translations)
            .Where(x => requestedAllergenIds.Contains(x.Id) && x.IsActive)
            .ToListAsync(ct);
        var activeAllergenIds = activeAllergens.Select(x => x.Id).ToHashSet();
        var invalidAllergenIds = requestedAllergenIds
            .Where(id => !activeAllergenIds.Contains(id))
            .Order()
            .ToArray();
        if (invalidAllergenIds.Length > 0)
            return new(null, invalidAllergenIds);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var profile = await ProfileQuery(tracking: true)
            .SingleOrDefaultAsync(x => x.UserId == userId, ct);

        if (profile is null)
        {
            profile = new CustomerProfile
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CreatedAt = now,
                CreatedBy = userId,
                IsActive = true,
                RowVersion = 1
            };
            db.CustomerProfiles.Add(profile);
        }
        else
        {
            profile.RowVersion++;
        }

        profile.GenderCode = NormalizeCode(request.GenderCode);
        profile.DateOfBirth = request.DateOfBirth;
        profile.HeightCm = request.HeightCm;
        profile.WeightKg = request.WeightKg;
        (profile.Bmi, profile.BmiCategoryCode) =
            CustomerProfileCalculations.Bmi(request.HeightCm, request.WeightKg);
        profile.GoalCode = NormalizeCode(request.GoalCode);
        profile.DailyRoutineCode = NormalizeCode(request.DailyRoutineCode);
        profile.ActivityLevelCode = NormalizeCode(request.ActivityLevelCode);
        profile.PreferredLanguage = request.PreferredLanguage.Trim().ToLowerInvariant();
        profile.OnboardingStatus = request.OnboardingStatus.Trim().ToUpperInvariant();
        if (profile.OnboardingStatus == "COMPLETED")
            profile.OnboardingCompletedAt ??= now;
        profile.UpdatedAt = now;
        profile.UpdatedBy = userId;

        SynchronizePreferences(profile, request.Preferences, now);
        SynchronizeAllergens(profile, request.Allergens, activeAllergens, userId, now);
        var nutritionTarget = RecalculateNutritionTarget(profile, today, userId, now);

        await db.SaveChangesAsync(ct);
        if (nutritionTarget is not null)
        {
            nutritionTarget.CustomerProfileId = profile.Id;
            nutritionTarget.CustomerProfile = profile;
            db.CustomerNutritionTargets.Add(nutritionTarget);
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);

        logger.LogInformation(
            "Customer profile {ProfileId} was saved for authenticated user {UserId} with onboarding status {OnboardingStatus}",
            profile.Id,
            userId,
            profile.OnboardingStatus);
        return new(ToResponse(profile, today), []);
    }

    public async Task<CustomerProfileResponse> UpdatePreferredNameAsync(
        Guid userId,
        string preferredName,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var profile = await ProfileQuery(tracking: true)
            .SingleOrDefaultAsync(x => x.UserId == userId, ct);

        if (profile is null)
        {
            profile = new CustomerProfile
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CreatedAt = now,
                CreatedBy = userId,
                IsActive = true,
                RowVersion = 1
            };
            db.CustomerProfiles.Add(profile);
        }
        else
        {
            profile.RowVersion++;
        }

        profile.PreferredName = preferredName.Trim();
        profile.UpdatedAt = now;
        profile.UpdatedBy = userId;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Preferred name was saved for customer profile {ProfileId} and authenticated user {UserId}",
            profile.Id,
            userId);
        return ToResponse(profile, DateOnly.FromDateTime(now.UtcDateTime));
    }

    private IQueryable<CustomerProfile> ProfileQuery(bool tracking)
    {
        var query = db.CustomerProfiles
            .Include(x => x.NutritionTargets)
            .Include(x => x.Preferences)
            .Include(x => x.Allergens)
                .ThenInclude(x => x.Allergen)
                    .ThenInclude(x => x.Translations)
            .AsSplitQuery();
        return tracking ? query : query.AsNoTracking();
    }

    private void SynchronizePreferences(
        CustomerProfile profile,
        IReadOnlyCollection<CustomerPreferenceRequest> requested,
        DateTimeOffset now)
    {
        var requestedByCode = requested.ToDictionary(
            x => x.PreferenceCode.Trim(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var existing in profile.Preferences.ToArray())
        {
            if (!requestedByCode.Remove(existing.PreferenceCode, out var item))
            {
                db.CustomerProfilePreferences.Remove(existing);
                continue;
            }

            existing.PreferenceCode = item.PreferenceCode.Trim();
            existing.PreferenceType = NormalizeCode(item.PreferenceType);
            existing.PreferencePriority = item.PreferencePriority;
            existing.UpdatedAt = now;
        }

        foreach (var item in requestedByCode.Values)
        {
            profile.Preferences.Add(new CustomerProfilePreference
            {
                Id = Guid.NewGuid(),
                PreferenceCode = item.PreferenceCode.Trim(),
                PreferenceType = NormalizeCode(item.PreferenceType),
                PreferencePriority = item.PreferencePriority,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
    }

    private void SynchronizeAllergens(
        CustomerProfile profile,
        IReadOnlyCollection<CustomerAllergenRequest> requested,
        IReadOnlyCollection<Allergen> activeAllergens,
        Guid userId,
        DateTimeOffset now)
    {
        var requestedById = requested.ToDictionary(x => x.AllergenId);
        foreach (var existing in profile.Allergens.ToArray())
        {
            if (!requestedById.Remove(existing.AllergenId, out var item))
            {
                db.CustomerProfileAllergens.Remove(existing);
                continue;
            }

            existing.SeverityCode = NormalizeCode(item.SeverityCode);
            existing.MedicallyConfirmed = item.MedicallyConfirmed;
            existing.Notes = NormalizeText(item.Notes);
            existing.UpdatedAt = now;
            existing.UpdatedBy = userId;
        }

        var allergensById = activeAllergens.ToDictionary(x => x.Id);
        foreach (var item in requestedById.Values)
        {
            profile.Allergens.Add(new CustomerProfileAllergen
            {
                Id = Guid.NewGuid(),
                AllergenId = item.AllergenId,
                Allergen = allergensById[item.AllergenId],
                SeverityCode = NormalizeCode(item.SeverityCode),
                MedicallyConfirmed = item.MedicallyConfirmed,
                Notes = NormalizeText(item.Notes),
                CreatedAt = now,
                UpdatedAt = now,
                CreatedBy = userId,
                UpdatedBy = userId
            });
        }
    }

    private CustomerNutritionTarget? RecalculateNutritionTarget(
        CustomerProfile profile,
        DateOnly today,
        Guid userId,
        DateTimeOffset now)
    {
        foreach (var current in profile.NutritionTargets.Where(x => x.IsCurrent))
        {
            current.IsCurrent = false;
            current.UpdatedAt = now;
            current.UpdatedBy = userId;
            current.RowVersion++;
        }

        CustomerNutritionCalculationResult? calculation;
        try
        {
            calculation = nutritionCalculator.Calculate(new(
                profile.GenderCode,
                profile.DateOfBirth,
                profile.HeightCm,
                profile.WeightKg,
                profile.GoalCode,
                profile.ActivityLevelCode,
                today));
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Nutrition target calculation failed for customer profile {ProfileId}",
                profile.Id);
            return null;
        }

        if (calculation is null)
            return null;

        return new CustomerNutritionTarget
        {
            Id = Guid.NewGuid(),
            DailyCaloriesKcal = calculation.DailyCaloriesKcal,
            DailyProteinG = calculation.DailyProteinG,
            DailyCarbohydratesG = calculation.DailyCarbohydratesG,
            DailyFatG = calculation.DailyFatG,
            DailyFiberG = calculation.DailyFiberG,
            DailyWaterMl = calculation.DailyWaterMl,
            CalculationMethod = calculation.CalculationMethod,
            CalculationVersion = calculation.CalculationVersion,
            CalculatedAt = now,
            IsCurrent = true,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = userId,
            UpdatedBy = userId,
            RowVersion = 1
        };
    }

    private static CustomerProfileResponse ToResponse(
        CustomerProfile profile,
        DateOnly today)
    {
        var language = profile.PreferredLanguage;
        var currentTarget = profile.NutritionTargets
            .Where(x => x.IsCurrent)
            .OrderByDescending(x => x.CalculatedAt)
            .FirstOrDefault();
        return new(
            profile.Id,
            profile.UserId!.Value,
            profile.PreferredName,
            profile.GenderCode,
            profile.DateOfBirth,
            profile.DateOfBirth is null
                ? null
                : CustomerProfileCalculations.Age(profile.DateOfBirth.Value, today),
            profile.HeightCm,
            profile.WeightKg,
            profile.Bmi,
            profile.BmiCategoryCode,
            profile.GoalCode,
            profile.DailyRoutineCode,
            profile.ActivityLevelCode,
            profile.PreferredLanguage,
            profile.OnboardingStatus,
            profile.OnboardingCompletedAt,
            profile.IsActive,
            currentTarget is null
                ? null
                : new CustomerNutritionTargetResponse(
                    currentTarget.DailyCaloriesKcal,
                    currentTarget.DailyProteinG,
                    currentTarget.DailyCarbohydratesG,
                    currentTarget.DailyFatG,
                    currentTarget.DailyFiberG,
                    currentTarget.DailyWaterMl,
                    currentTarget.CalculationMethod,
                    currentTarget.CalculationVersion,
                    currentTarget.CalculatedAt),
            profile.Preferences
                .OrderByDescending(x => x.PreferencePriority)
                .ThenBy(x => x.PreferenceCode)
                .Select(x => new CustomerPreferenceResponse(
                    x.Id,
                    x.PreferenceCode,
                    x.PreferenceType,
                    x.PreferencePriority))
                .ToArray(),
            profile.Allergens
                .OrderBy(x => x.Allergen.Code)
                .Select(x => new CustomerAllergenResponse(
                    x.Id,
                    x.AllergenId,
                    x.Allergen.Code,
                    ResolveAllergenName(x.Allergen, language),
                    x.SeverityCode,
                    x.MedicallyConfirmed,
                    x.Notes))
                .ToArray(),
            profile.CreatedAt,
            profile.UpdatedAt,
            profile.RowVersion);
    }

    private static string ResolveAllergenName(Allergen allergen, string language) =>
        allergen.Translations
            .FirstOrDefault(x => x.LanguageCode.Equals(language, StringComparison.OrdinalIgnoreCase))
            ?.Name
        ?? allergen.Translations
            .FirstOrDefault(x => x.LanguageCode.Equals("en", StringComparison.OrdinalIgnoreCase))
            ?.Name
        ?? allergen.Translations.FirstOrDefault()?.Name
        ?? allergen.Code;

    private DateOnly Today() =>
        DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    private static string? NormalizeCode(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private static string? NormalizeText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
