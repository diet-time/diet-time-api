using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Persistence;

public sealed class MealPackageService(DietTimeDbContext db, TimeProvider clock) : IMealPackageService
{
    public async Task<IReadOnlyList<MealPackageOptionResponse>> GetAsync(bool activeOnly, CancellationToken ct)
    {
        var query = db.MealPackageOptions.AsNoTracking().AsQueryable();
        if (activeOnly) query = query.Where(x => x.IsActive);
        var rows = await query.OrderBy(x => x.DisplayOrder).ThenBy(x => x.Name).ToListAsync(ct);
        return rows.Select(Map).ToArray();
    }

    public async Task<MealPackageOptionResponse?> GetAsync(Guid id, CancellationToken ct)
    {
        var row = await db.MealPackageOptions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return row is null ? null : Map(row);
    }

    public async Task<Guid> CreateAsync(UpsertMealPackageOptionRequest request, Guid? userId, CancellationToken ct)
    {
        Validate(request);
        var name = request.Name.Trim();
        if (await db.MealPackageOptions.AnyAsync(x => x.Name.ToLower() == name.ToLower(), ct))
            throw Conflict("duplicate_package_name", "A package option with this name already exists.");
        if (await db.MealPackageOptions.AnyAsync(x => x.MealCount == request.MealCount && x.SnackCount == request.SnackCount, ct))
            throw Conflict("duplicate_package_configuration", "A package option with these meal and snack counts already exists.");
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
        if (await db.MealPackageOptions.AnyAsync(x => x.Id != id && x.MealCount == request.MealCount && x.SnackCount == request.SnackCount, ct))
            throw Conflict("duplicate_package_configuration", "A package option with these meal and snack counts already exists.");
        row.Name = name; row.MealCount = request.MealCount; row.SnackCount = request.SnackCount; row.DisplayOrder = request.DisplayOrder; row.IsActive = request.IsActive; row.UpdatedAt = clock.GetUtcNow(); row.UpdatedBy = userId;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetStatusAsync(Guid id, bool isActive, Guid? userId, CancellationToken ct)
    {
        var row = await db.MealPackageOptions.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw NotFound("The package option does not exist.");
        if (isActive && await db.MealPackageOptions.AnyAsync(x => x.Id != id && x.IsActive && x.MealCount == row.MealCount && x.SnackCount == row.SnackCount, ct))
            throw Conflict("duplicate_package_configuration", "An active package option with these meal and snack counts already exists.");
        row.IsActive = isActive; row.UpdatedAt = clock.GetUtcNow(); row.UpdatedBy = userId;
        await db.SaveChangesAsync(ct);
    }

    private static MealPackageOptionResponse Map(MealPackageOption x) => new(x.Id, x.Name, x.MealCount, x.SnackCount, x.DisplayOrder, x.IsActive);
    private static void Validate(UpsertMealPackageOptionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw BadRequest("name_required", "Name is required.");
        if (request.MealCount <= 0) throw BadRequest("invalid_meal_count", "Meal count must be greater than zero.");
        if (request.SnackCount < 0) throw BadRequest("invalid_snack_count", "Snack count cannot be negative.");
        if (request.DisplayOrder < 0) throw BadRequest("invalid_display_order", "Display order cannot be negative.");
    }
    private static MealConfigurationException BadRequest(string code, string message) => new(400, code, message);
    private static MealConfigurationException NotFound(string message) => new(404, "not_found", message);
    private static MealConfigurationException Conflict(string code, string message) => new(409, code, message);
}
