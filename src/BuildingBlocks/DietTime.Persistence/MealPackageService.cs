using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Persistence;

public sealed class MealPackageService(DietTimeDbContext db, TimeProvider clock) : IMealPackageService
{
    public async Task<IReadOnlyList<MealPackageOptionResponse>> GetAsync(bool activeOnly, CancellationToken ct)
    {
        var query = db.MealPackageOptions.AsNoTracking().Include(x => x.MealTypes).ThenInclude(x => x.MealType).AsQueryable();
        if (activeOnly) query = query.Where(x => x.IsActive);
        var rows = await query.OrderBy(x => x.DisplayOrder).ThenBy(x => x.Name).ToListAsync(ct);
        return rows.Select(Map).ToArray();
    }

    public async Task<MealPackageOptionResponse?> GetAsync(Guid id, CancellationToken ct)
    {
        var row = await db.MealPackageOptions.AsNoTracking().Include(x => x.MealTypes).ThenInclude(x => x.MealType)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        return row is null ? null : Map(row);
    }

    public async Task<Guid> CreateAsync(UpsertMealPackageOptionRequest request, Guid? userId, CancellationToken ct)
    {
        Validate(request);
        var name = request.Name.Trim();
        if (await db.MealPackageOptions.AnyAsync(x => x.Name.ToLower() == name.ToLower(), ct))
            throw Conflict("duplicate_package_name", "A package option with this name already exists.");
        var now = clock.GetUtcNow();
        var row = new MealPackageOption { Id = Guid.NewGuid(), Name = name, MealCount = request.MealCount, SnackCount = request.SnackCount, DisplayOrder = request.DisplayOrder, IsActive = request.IsActive, CreatedAt = now, UpdatedAt = now, CreatedBy = userId, UpdatedBy = userId };
        db.MealPackageOptions.Add(row);
        await db.SaveChangesAsync(ct);
        return row.Id;
    }

    public async Task UpdateAsync(Guid id, UpsertMealPackageOptionRequest request, Guid? userId, CancellationToken ct)
    {
        Validate(request);
        var row = await db.MealPackageOptions.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw NotFound("The package option does not exist.");
        var name = request.Name.Trim();
        if (await db.MealPackageOptions.AnyAsync(x => x.Id != id && x.Name.ToLower() == name.ToLower(), ct))
            throw Conflict("duplicate_package_name", "A package option with this name already exists.");
        row.Name = name; row.MealCount = request.MealCount; row.SnackCount = request.SnackCount; row.DisplayOrder = request.DisplayOrder; row.IsActive = request.IsActive; row.UpdatedAt = clock.GetUtcNow(); row.UpdatedBy = userId;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetStatusAsync(Guid id, bool isActive, Guid? userId, CancellationToken ct)
    {
        var row = await db.MealPackageOptions.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw NotFound("The package option does not exist.");
        row.IsActive = isActive; row.UpdatedAt = clock.GetUtcNow(); row.UpdatedBy = userId;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<PackageMealTypeResponse>> GetMealTypesAsync(Guid packageOptionId, CancellationToken ct)
    {
        if (!await db.MealPackageOptions.AnyAsync(x => x.Id == packageOptionId, ct)) throw NotFound("The package option does not exist.");
        var configured = await db.MealPackageOptionTypes.AsNoTracking().Where(x => x.PackageOptionId == packageOptionId).ToDictionaryAsync(x => x.MealTypeId, ct);
        var types = await db.MealTypes.AsNoTracking().OrderBy(x => x.DisplayOrder).ThenBy(x => x.Code).ToListAsync(ct);
        return types.Select(x => configured.TryGetValue(x.Id, out var value)
            ? new PackageMealTypeResponse(x.Id, x.Code, value.IsRequired, value.MaxQuantity, value.DisplayOrder, value.IsActive)
            : new PackageMealTypeResponse(x.Id, x.Code, false, 1, x.DisplayOrder, false)).ToArray();
    }

    public async Task UpdateMealTypesAsync(Guid packageOptionId, UpdatePackageMealTypesRequest request, CancellationToken ct)
    {
        if (!await db.MealPackageOptions.AnyAsync(x => x.Id == packageOptionId, ct)) throw NotFound("The package option does not exist.");
        if (request.MealTypes is null) throw BadRequest("meal_types_required", "Meal types are required.");
        if (request.MealTypes.GroupBy(x => x.MealTypeId).Any(x => x.Count() > 1)) throw BadRequest("duplicate_meal_type", "Duplicate meal types are not permitted for the same package.");
        if (request.MealTypes.Any(x => x.MaxQuantity <= 0)) throw BadRequest("invalid_max_quantity", "Maximum quantity must be greater than zero.");
        var ids = request.MealTypes.Select(x => x.MealTypeId).ToArray();
        var types = await db.MealTypes.Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        if (types.Count != ids.Length) throw NotFound("The selected meal type does not exist.");
        if (types.Any(x => !x.IsActive)) throw BadRequest("inactive_meal_type", "The selected meal type is inactive.");
        var now = clock.GetUtcNow();
        var existing = await db.MealPackageOptionTypes.Where(x => x.PackageOptionId == packageOptionId).ToListAsync(ct);
        foreach (var row in existing) row.IsActive = false;
        foreach (var item in request.MealTypes)
        {
            var row = existing.SingleOrDefault(x => x.MealTypeId == item.MealTypeId);
            if (row is null) db.MealPackageOptionTypes.Add(new() { Id = Guid.NewGuid(), PackageOptionId = packageOptionId, MealTypeId = item.MealTypeId, IsRequired = item.IsRequired, MaxQuantity = item.MaxQuantity, DisplayOrder = item.DisplayOrder, IsActive = true, CreatedAt = now, UpdatedAt = now });
            else { row.IsRequired = item.IsRequired; row.MaxQuantity = item.MaxQuantity; row.DisplayOrder = item.DisplayOrder; row.IsActive = true; row.UpdatedAt = now; }
        }
        await db.SaveChangesAsync(ct);
    }

    private static MealPackageOptionResponse Map(MealPackageOption x) => new(x.Id, x.Name, x.MealCount, x.SnackCount, x.DisplayOrder, x.IsActive,
        x.MealTypes.Where(t => t.IsActive).OrderBy(t => t.DisplayOrder).Select(t => new PackageMealTypeResponse(t.MealTypeId, t.MealType.Code, t.IsRequired, t.MaxQuantity, t.DisplayOrder)).ToArray());
    private static void Validate(UpsertMealPackageOptionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw BadRequest("name_required", "Name is required.");
        if (request.MealCount <= 0) throw BadRequest("invalid_meal_count", "Meal count must be greater than zero.");
        if (request.SnackCount < 0) throw BadRequest("invalid_snack_count", "Snack count cannot be negative.");
    }
    private static MealConfigurationException BadRequest(string code, string message) => new(400, code, message);
    private static MealConfigurationException NotFound(string message) => new(404, "not_found", message);
    private static MealConfigurationException Conflict(string code, string message) => new(409, code, message);
}
