using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DietTime.Persistence;

public sealed class GuestProfileService(
    DietTimeDbContext db,
    ICustomerNutritionCalculator nutritionCalculator,
    GuestProfileOptions options,
    TimeProvider clock,
    ILogger<GuestProfileService> logger) : IGuestProfileService
{
    public async Task<GuestCustomerProfileResponse?> GetAsync(
        Guid profileId,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var profile = await ProfileQuery(tracking: false)
            .SingleOrDefaultAsync(x =>
                x.Id == profileId &&
                x.UserId == null &&
                x.GuestTokenHash != null &&
                x.GuestTokenExpiresAt > now &&
                x.IsActive,
                ct);
        return profile is null ? null : ToResponse(profile, Today(now));
    }

    public async Task<GuestProfileUpsertResult> UpsertAsync(
        string tokenHash,
        UpsertGuestProfileRequest request,
        CancellationToken ct)
    {
        const int maximumAttempts = 5;
        var requestedAllergenIds = request.Allergens
            .Select(x => x.AllergenId)
            .Distinct()
            .ToArray();
        var activeAllergens = await LoadActiveAllergensAsync(requestedAllergenIds, ct);
        var activeIds = activeAllergens.Select(x => x.Id).ToHashSet();
        var invalidIds = requestedAllergenIds
            .Where(id => !activeIds.Contains(id))
            .Order()
            .ToArray();
        if (invalidIds.Length > 0)
            return new(null, invalidIds);

        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                return await UpsertCoreAsync(tokenHash, request, activeAllergens, ct);
            }
            catch (DbUpdateConcurrencyException) when (attempt < maximumAttempts)
            {
                logger.LogWarning(
                    "Guest profile update encountered a concurrency conflict; retrying attempt {Attempt} of {MaximumAttempts}",
                    attempt + 1,
                    maximumAttempts);
            }
            catch (DbUpdateException exception)
                when (attempt < maximumAttempts && IsUniqueViolation(exception))
            {
                db.ChangeTracker.Clear();
                if (!await db.CustomerProfiles.AsNoTracking()
                        .AnyAsync(x => x.GuestTokenHash == tokenHash, ct))
                {
                    throw;
                }

                logger.LogWarning(
                    "Concurrent guest profile creation was detected; retrying attempt {Attempt} of {MaximumAttempts}",
                    attempt + 1,
                    maximumAttempts);
            }

            db.ChangeTracker.Clear();
            await Task.Delay(
                TimeSpan.FromMilliseconds(Random.Shared.Next(10, 31) * attempt),
                ct);
            activeAllergens = await LoadActiveAllergensAsync(requestedAllergenIds, ct);
        }

        throw new DbUpdateConcurrencyException(
            "Guest profile could not be saved after repeated concurrent updates.");
    }

    private async Task<GuestProfileUpsertResult> UpsertCoreAsync(
        string tokenHash,
        UpsertGuestProfileRequest request,
        IReadOnlyCollection<Allergen> activeAllergens,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        var today = Today(now);
        var profile = await ProfileQuery(tracking: true)
            .SingleOrDefaultAsync(x => x.GuestTokenHash == tokenHash, ct);

        if (profile is null)
        {
            profile = new CustomerProfile
            {
                Id = Guid.NewGuid(),
                UserId = null,
                GuestTokenHash = tokenHash,
                GuestTokenExpiresAt = now.AddDays(options.TokenExpiryDays),
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
                RowVersion = 1
            };
            db.CustomerProfiles.Add(profile);
        }
        else
        {
            if (profile.UserId is not null ||
                !profile.IsActive ||
                profile.GuestTokenExpiresAt is null ||
                profile.GuestTokenExpiresAt <= now)
            {
                throw new InvalidGuestSessionException();
            }
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
        if (profile.OnboardingStatus is "PROFILE_COMPLETED" or "PLAN_SELECTED")
            profile.OnboardingCompletedAt ??= now;
        profile.UpdatedAt = now;

        SynchronizePreferences(profile, request.Preferences, now);
        SynchronizeAllergens(profile, request.Allergens, activeAllergens, now);
        var newTarget = RecalculateNutritionTarget(profile, today, now);

        await db.SaveChangesAsync(ct);
        if (newTarget is not null)
        {
            newTarget.CustomerProfileId = profile.Id;
            newTarget.CustomerProfile = profile;
            db.CustomerNutritionTargets.Add(newTarget);
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);

        logger.LogInformation(
            "Guest profile {ProfileId} saved with onboarding status {OnboardingStatus}",
            profile.Id,
            profile.OnboardingStatus);
        return new(ToResponse(profile, today), []);
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
        var remaining = requested.ToDictionary(
            x => x.PreferenceCode.Trim(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var existing in profile.Preferences.ToArray())
        {
            if (!remaining.Remove(existing.PreferenceCode, out var item))
            {
                db.CustomerProfilePreferences.Remove(existing);
                continue;
            }
            existing.PreferenceCode = item.PreferenceCode.Trim();
            existing.PreferenceType = NormalizeCode(item.PreferenceType);
            existing.PreferencePriority = item.PreferencePriority;
            existing.UpdatedAt = now;
        }

        foreach (var item in remaining.Values)
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
        DateTimeOffset now)
    {
        var remaining = requested.ToDictionary(x => x.AllergenId);
        foreach (var existing in profile.Allergens.ToArray())
        {
            if (!remaining.Remove(existing.AllergenId, out var item))
            {
                db.CustomerProfileAllergens.Remove(existing);
                continue;
            }
            existing.SeverityCode = NormalizeCode(item.SeverityCode);
            existing.MedicallyConfirmed = item.MedicallyConfirmed;
            existing.Notes = NormalizeText(item.Notes);
            existing.UpdatedAt = now;
        }

        var allergensById = activeAllergens.ToDictionary(x => x.Id);
        foreach (var item in remaining.Values)
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
                UpdatedAt = now
            });
        }
    }

    private CustomerNutritionTarget? RecalculateNutritionTarget(
        CustomerProfile profile,
        DateOnly today,
        DateTimeOffset now)
    {
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
                "Nutrition calculation failed for guest profile {ProfileId}",
                profile.Id);
            return null;
        }

        var current = profile.NutritionTargets
            .Where(x => x.IsCurrent)
            .OrderByDescending(x => x.CalculatedAt)
            .FirstOrDefault();
        if (calculation is not null && current is not null && Same(current, calculation))
            return null;

        foreach (var target in profile.NutritionTargets.Where(x => x.IsCurrent))
        {
            target.IsCurrent = false;
            target.UpdatedAt = now;
            target.RowVersion++;
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
            RowVersion = 1
        };
    }

    private static bool Same(
        CustomerNutritionTarget current,
        CustomerNutritionCalculationResult calculated) =>
        current.DailyCaloriesKcal == calculated.DailyCaloriesKcal &&
        current.DailyProteinG == calculated.DailyProteinG &&
        current.DailyCarbohydratesG == calculated.DailyCarbohydratesG &&
        current.DailyFatG == calculated.DailyFatG &&
        current.DailyFiberG == calculated.DailyFiberG &&
        current.DailyWaterMl == calculated.DailyWaterMl &&
        current.CalculationMethod == calculated.CalculationMethod &&
        current.CalculationVersion == calculated.CalculationVersion;

    private static GuestCustomerProfileResponse ToResponse(
        CustomerProfile profile,
        DateOnly today)
    {
        var current = profile.NutritionTargets
            .Where(x => x.IsCurrent)
            .OrderByDescending(x => x.CalculatedAt)
            .FirstOrDefault();
        return new(
            profile.Id,
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
            profile.IsActive,
            profile.GuestTokenExpiresAt!.Value,
            current is null
                ? null
                : new GuestNutritionTargetResponse(
                    current.DailyCaloriesKcal,
                    current.DailyProteinG,
                    current.DailyCarbohydratesG,
                    current.DailyFatG,
                    current.DailyFiberG,
                    current.DailyWaterMl,
                    current.CalculationMethod,
                    current.CalculationVersion,
                    current.CalculatedAt),
            profile.Preferences
                .OrderByDescending(x => x.PreferencePriority)
                .ThenBy(x => x.PreferenceCode)
                .Select(x => new GuestPreferenceResponse(
                    x.Id,
                    x.PreferenceCode,
                    x.PreferenceType,
                    x.PreferencePriority))
                .ToArray(),
            profile.Allergens
                .OrderBy(x => x.Allergen.Code)
                .Select(x => new GuestAllergenResponse(
                    x.Id,
                    x.AllergenId,
                    x.Allergen.Code,
                    ResolveName(x.Allergen, profile.PreferredLanguage),
                    x.SeverityCode,
                    x.MedicallyConfirmed,
                    x.Notes))
                .ToArray(),
            profile.CreatedAt,
            profile.UpdatedAt,
            profile.RowVersion);
    }

    private static string ResolveName(Allergen allergen, string language) =>
        allergen.Translations
            .FirstOrDefault(x => x.LanguageCode.Equals(language, StringComparison.OrdinalIgnoreCase))
            ?.Name
        ?? allergen.Translations
            .FirstOrDefault(x => x.LanguageCode.Equals("en", StringComparison.OrdinalIgnoreCase))
            ?.Name
        ?? allergen.Code;

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation
        };

    private Task<List<Allergen>> LoadActiveAllergensAsync(
        IReadOnlyCollection<Guid> allergenIds,
        CancellationToken ct) =>
        db.Allergens
            .Include(x => x.Translations)
            .Where(x => allergenIds.Contains(x.Id) && x.IsActive)
            .ToListAsync(ct);

    private static DateOnly Today(DateTimeOffset now) =>
        DateOnly.FromDateTime(now.UtcDateTime);
    private static string? NormalizeCode(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
    private static string? NormalizeText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
