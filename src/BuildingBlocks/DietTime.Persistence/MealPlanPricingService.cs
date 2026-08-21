using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Persistence;

public sealed class MealPlanPricingService(DietTimeDbContext db, TimeProvider clock) : IMealPlanPricingService
{
    public async Task<IReadOnlyList<MealPlanPricingResponse>> GetAsync(Guid? mealPlanId, string? durationId, Guid? packageOptionId, bool activeOnly, CancellationToken ct)
    {
        var query = db.MealPlanPrices.AsNoTracking()
            .Where(x => x.PackageOptionId != null)
            .Include(x => x.Plan).ThenInclude(x => x.Translations)
            .Include(x => x.PackageOption).AsQueryable();
        if (mealPlanId.HasValue) query = query.Where(x => x.MealPlanTemplateId == mealPlanId);
        if (!string.IsNullOrWhiteSpace(durationId))
        {
            var code = NormalizeDurationId(durationId);
            var duration = await db.MealPlanPricePackages.AsNoTracking().SingleOrDefaultAsync(x => x.Code == code, ct);
            if (duration is null) return [];
            query = query.Where(x => x.PackageCode == code || (x.PackageCode == null && x.DurationDays == duration.DurationDays));
        }
        if (packageOptionId.HasValue) query = query.Where(x => x.PackageOptionId == packageOptionId);
        if (activeOnly) query = query.Where(x => x.IsActive);
        var rows = await query.OrderBy(x => x.Plan.Code).ThenBy(x => x.DurationDays).ThenBy(x => x.PackageOption!.DisplayOrder).ToListAsync(ct);
        var durations = await db.MealPlanPricePackages.AsNoTracking().OrderBy(x => x.DisplayOrder).ToListAsync(ct);
        return rows.Select(row => (Row: row, Duration: ResolveDuration(row, durations)))
            .Where(x => x.Duration is not null)
            .Select(x => Map(x.Row, x.Duration!)).ToArray();
    }

    public async Task<Guid> CreateAsync(UpsertMealPlanPricingRequest request, Guid? userId, CancellationToken ct)
    {
        var masters = await ValidateMasters(request, ct);
        await EnsureNoConflict(request, null, ct);
        var now = clock.GetUtcNow();
        var row = new MealPlanPrice
        {
            Id = Guid.NewGuid(), MealPlanTemplateId = request.MealPlanId, PackageCode = masters.Duration.Code,
            PackageOptionId = request.PackageOptionId, DurationDays = masters.Duration.DurationDays,
            MealsPerDay = masters.Package.MealCount, SnacksPerDay = masters.Package.SnackCount,
            Amount = request.Price, CurrencyCode = NormalizeCurrency(request.CurrencyCode), IsActive = request.IsActive,
            EffectiveFrom = now, CreatedAt = now, UpdatedAt = now, CreatedBy = userId, UpdatedBy = userId
        };
        db.MealPlanPrices.Add(row);
        await db.SaveChangesAsync(ct);
        return row.Id;
    }

    public async Task UpdateAsync(Guid id, UpsertMealPlanPricingRequest request, Guid? userId, CancellationToken ct)
    {
        var row = await db.MealPlanPrices.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new MealConfigurationException(404, "price_not_found", "The meal plan price does not exist.");
        var masters = await ValidateMasters(request, ct);
        await EnsureNoConflict(request, id, ct);
        row.MealPlanTemplateId = request.MealPlanId; row.PackageCode = masters.Duration.Code; row.PackageOptionId = request.PackageOptionId;
        row.DurationDays = masters.Duration.DurationDays; row.MealsPerDay = masters.Package.MealCount; row.SnacksPerDay = masters.Package.SnackCount;
        row.Amount = request.Price; row.CurrencyCode = NormalizeCurrency(request.CurrencyCode); row.IsActive = request.IsActive; row.UpdatedAt = clock.GetUtcNow(); row.UpdatedBy = userId;
        await db.SaveChangesAsync(ct);
    }

    private async Task<(MealPlanPricePackage Duration, MealPackageOption Package)> ValidateMasters(UpsertMealPlanPricingRequest request, CancellationToken ct)
    {
        if (request.Price <= 0) throw new MealConfigurationException(400, "invalid_price", "Price must be greater than zero.");
        if (string.IsNullOrWhiteSpace(request.CurrencyCode) || request.CurrencyCode.Trim().Length != 3) throw new MealConfigurationException(400, "invalid_currency", "Currency code must contain three characters.");
        if (!await db.MealPlanTemplates.AnyAsync(x => x.Id == request.MealPlanId, ct)) throw new MealConfigurationException(404, "meal_plan_not_found", "The selected meal plan does not exist.");
        if (string.IsNullOrWhiteSpace(request.DurationId)) throw new MealConfigurationException(400, "duration_required", "Duration is required.");
        var durationCode = NormalizeDurationId(request.DurationId);
        var duration = await db.MealPlanPricePackages.SingleOrDefaultAsync(x => x.Code == durationCode, ct) ?? throw new MealConfigurationException(404, "duration_not_found", "The selected duration does not exist.");
        var package = await db.MealPackageOptions.SingleOrDefaultAsync(x => x.Id == request.PackageOptionId, ct) ?? throw new MealConfigurationException(404, "package_option_not_found", "The selected package option does not exist.");
        if (!duration.IsActive) throw new MealConfigurationException(400, "inactive_duration", "The selected duration is inactive.");
        if (!package.IsActive) throw new MealConfigurationException(400, "inactive_package_option", "The selected package option is inactive.");
        return (duration, package);
    }

    private async Task EnsureNoConflict(UpsertMealPlanPricingRequest request, Guid? excludedId, CancellationToken ct)
    {
        if (!request.IsActive) return;
        var durationCode = NormalizeDurationId(request.DurationId);
        if (await db.MealPlanPrices.AnyAsync(x => x.Id != excludedId && x.IsActive && x.MealPlanTemplateId == request.MealPlanId && x.PackageCode == durationCode && x.PackageOptionId == request.PackageOptionId, ct))
            throw new MealConfigurationException(409, "duplicate_package_pricing", "A price already exists for this meal plan, duration and package.");
    }

    private static MealPlanPricingResponse Map(MealPlanPrice x, MealPlanPricePackage duration) => new(x.Id, x.MealPlanTemplateId,
        x.Plan.Translations.FirstOrDefault(t => t.LanguageCode == "en")?.Name ?? x.Plan.Code,
        duration.Code, duration.NameEn, x.PackageOptionId!.Value, x.PackageOption!.Name,
        x.PackageOption.MealCount, x.PackageOption.SnackCount, x.Amount, x.CurrencyCode.Trim(), x.IsActive);
    private static string NormalizeCurrency(string value) => value.Trim().ToUpperInvariant();
    private static string NormalizeDurationId(string value) => string.Join('_', value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    private static MealPlanPricePackage? ResolveDuration(MealPlanPrice price, IReadOnlyList<MealPlanPricePackage> durations) =>
        !string.IsNullOrWhiteSpace(price.PackageCode)
            ? durations.FirstOrDefault(x => x.Code == price.PackageCode)
            : durations.FirstOrDefault(x => x.DurationDays == price.DurationDays);
}
