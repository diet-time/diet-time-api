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

    public Task<CustomerPersonalInfoResponse?> GetPersonalInfoAsync(
        Guid userId,
        CancellationToken ct) =>
        LoadPersonalInfoAsync(userId, ct);

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
        await AcquireProfileWriteLockAsync(userId, ct);
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var profile = await LoadProfileForUpdateAsync(userId, includeDetails: true, ct);

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

        if (nutritionTarget is not null)
        {
            nutritionTarget.CustomerProfileId = profile.Id;
            nutritionTarget.CustomerProfile = profile;
            db.CustomerNutritionTargets.Add(nutritionTarget);
        }
        await SaveChangesWithConcurrencyDiagnosticsAsync(userId, ct);
        await transaction.CommitAsync(ct);

        logger.LogInformation(
            "Customer profile {ProfileId} was saved for authenticated user {UserId} with onboarding status {OnboardingStatus}",
            profile.Id,
            userId,
            profile.OnboardingStatus);
        return new(ToResponse(profile, today), []);
    }

    public async Task<CustomerPersonalInfoResponse?> UpdatePersonalInfoAsync(
        Guid userId,
        UpdateCustomerPersonalInfoRequest request,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await AcquireProfileWriteLockAsync(userId, ct);
        var profile = await LoadProfileForUpdateAsync(userId, includeDetails: false, ct);
        if (profile is not null && !profile.IsActive)
            profile = null;
        if (profile is null)
            return null;

        var now = clock.GetUtcNow();
        profile.PreferredName = request.FullName.Trim();
        profile.DateOfBirth = request.DateOfBirth;
        profile.GenderCode = request.Gender.Trim().ToUpperInvariant();
        profile.UpdatedAt = now;
        profile.UpdatedBy = userId;
        profile.RowVersion++;

        await SaveChangesWithConcurrencyDiagnosticsAsync(userId, ct);
        await transaction.CommitAsync(ct);

        logger.LogInformation(
            "Customer personal information was updated. ProfileId={ProfileId} UserId={UserId}",
            profile.Id, userId);
        return await LoadPersonalInfoAsync(userId, ct);
    }

    public async Task<CustomerProfileResponse> UpdatePreferredNameAsync(
        Guid userId,
        string preferredName,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await AcquireProfileWriteLockAsync(userId, ct);
        var now = clock.GetUtcNow();
        var profile = await LoadProfileForUpdateAsync(userId, includeDetails: true, ct);

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
        await SaveChangesWithConcurrencyDiagnosticsAsync(userId, ct);
        await transaction.CommitAsync(ct);

        logger.LogInformation(
            "Preferred name was saved for customer profile {ProfileId} and authenticated user {UserId}",
            profile.Id,
            userId);
        return ToResponse(profile, DateOnly.FromDateTime(now.UtcDateTime));
    }

    private Task AcquireProfileWriteLockAsync(Guid userId, CancellationToken ct)
    {
        var lockKey = $"customer-profile:{userId:D}";
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct);
    }

    private async Task<CustomerProfile?> LoadProfileForUpdateAsync(
        Guid userId,
        bool includeDetails,
        CancellationToken ct)
    {
        var profile = await db.CustomerProfiles
            .FromSqlInterpolated(
                $"SELECT * FROM public.customer_profiles WHERE user_id = {userId} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (profile is null || !includeDetails)
            return profile;

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM public.customer_nutrition_targets WHERE customer_profile_id = {profile.Id} FOR UPDATE",
            ct);
        await db.Entry(profile).Collection(x => x.NutritionTargets).LoadAsync(ct);
        await db.Entry(profile).Collection(x => x.Preferences).LoadAsync(ct);
        await db.Entry(profile).Collection(x => x.Allergens).Query()
            .Include(x => x.Allergen)
            .ThenInclude(x => x.Translations)
            .LoadAsync(ct);
        return profile;
    }

    private async Task SaveChangesWithConcurrencyDiagnosticsAsync(
        Guid userId,
        CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            var conflictingEntries = exception.Entries.Select(entry =>
            {
                var primaryKey = entry.Metadata.FindPrimaryKey();
                var key = primaryKey is null
                    ? "unknown"
                    : string.Join(",", primaryKey.Properties.Select(property =>
                        entry.Property(property.Name).CurrentValue?.ToString() ?? "null"));
                return $"{entry.Metadata.ClrType.Name}:{key}";
            }).ToArray();
            logger.LogWarning(
                exception,
                "Customer profile save encountered an optimistic concurrency conflict. UserId={UserId} ConflictingEntries={ConflictingEntries}",
                userId, conflictingEntries);
            throw;
        }
    }

    private async Task<CustomerPersonalInfoResponse?> LoadPersonalInfoAsync(
        Guid userId,
        CancellationToken ct)
    {
        var profile = await db.CustomerProfiles.AsNoTracking()
            .Include(x => x.Addresses.Where(address => address.IsActive))
            .SingleOrDefaultAsync(x => x.UserId == userId && x.IsActive, ct);
        if (profile is null)
            return null;

        var mobileNumber = await db.Users.AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.PhoneNumber)
            .SingleOrDefaultAsync(ct);
        return new CustomerPersonalInfoResponse(
            profile.Id,
            profile.PreferredName?.Trim() ?? string.Empty,
            mobileNumber,
            profile.DateOfBirth,
            GenderDisplayName(profile.GenderCode),
            profile.Addresses
                .OrderByDescending(address => address.IsDefault)
                .ThenByDescending(address => address.UpdatedAt)
                .ThenBy(address => address.Id)
                .Select(address => new CustomerProfileAddressResponse(
                    address.Id,
                    address.AddressName,
                    address.IsDefault,
                    address.UnitNumber,
                    address.BuildingNo,
                    address.StreetNo,
                    address.ZoneNo,
                    address.Area,
                    FirstNonEmpty(address.FormattedAddress, address.Directions),
                    address.Latitude,
                    address.Longitude))
                .ToArray());
    }

    private static string GenderDisplayName(string? genderCode) =>
        genderCode?.Trim().ToUpperInvariant() switch
        {
            CustomerGenderCodes.Male => "Male",
            CustomerGenderCodes.Female => "Female",
            _ => string.Empty
        };

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

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
