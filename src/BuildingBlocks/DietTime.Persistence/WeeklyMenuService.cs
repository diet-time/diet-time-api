using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Persistence;

public sealed class WeeklyMenuService(DietTimeDbContext db, TimeProvider clock) : IWeeklyMenuService
{
    public async Task<WeeklyMenuResponse> GetAsync(Guid mealPlanId, CancellationToken ct)
    {
        var plan = await db.MealPlanTemplates.AsNoTracking().Include(x => x.Translations).SingleOrDefaultAsync(x => x.Id == mealPlanId, ct)
            ?? throw NotFound("meal_plan_not_found", "The meal plan does not exist.");
        var days = await DayQuery(mealPlanId).OrderBy(x => x.DayOfWeek).ToListAsync(ct);
        return new(mealPlanId, Name(plan), days.Select(MapDay).ToArray());
    }

    public async Task<WeeklyMenuDayResponse> GetDayAsync(Guid mealPlanId, int dayOfWeek, CancellationToken ct)
    {
        ValidateDay(dayOfWeek);
        if (!await db.MealPlanTemplates.AnyAsync(x => x.Id == mealPlanId, ct)) throw NotFound("meal_plan_not_found", "The meal plan does not exist.");
        var day = await DayQuery(mealPlanId).SingleOrDefaultAsync(x => x.DayOfWeek == dayOfWeek, ct)
            ?? throw NotFound("weekly_menu_day_not_found", $"No {DayName(dayOfWeek)} menu is configured for this meal plan.");
        return MapDay(day);
    }

    public async Task UpdateDayAsync(Guid mealPlanId, int dayOfWeek, UpdateWeeklyMenuDayRequest request, Guid? userId, CancellationToken ct)
    {
        ValidateDay(dayOfWeek);
        if (!await db.MealPlanTemplates.AnyAsync(x => x.Id == mealPlanId, ct)) throw NotFound("meal_plan_not_found", "The meal plan does not exist.");
        if (request.MealTypes is null) throw BadRequest("meal_types_required", "Meal types are required.");
        if (request.MealTypes.GroupBy(x => x.MealTypeId).Any(x => x.Count() > 1)) throw BadRequest("duplicate_meal_type", "A meal type can only be configured once per weekday.");
        foreach (var type in request.MealTypes)
        {
            if (type.Items is null) throw BadRequest("items_required", "Menu items are required.");
            if (type.Items.GroupBy(x => x.MenuItemId).Any(x => x.Count() > 1)) throw BadRequest("duplicate_menu_item", "A meal cannot be configured more than once for the same weekday and meal type.");
            if (type.Items.Count(x => x.IsDefault) > 1) throw BadRequest("multiple_default_meals", $"Only one default meal can be configured for the selected meal type on {DayName(dayOfWeek)}.");
        }
        var typeIds = request.MealTypes.Select(x => x.MealTypeId).Distinct().ToArray();
        var types = await db.MealTypes.Where(x => typeIds.Contains(x.Id)).ToListAsync(ct);
        if (types.Count != typeIds.Length) throw NotFound("meal_type_not_found", "The selected meal type does not exist.");
        if (types.Any(x => !x.IsActive)) throw BadRequest("inactive_meal_type", "The selected meal type is inactive.");
        var itemIds = request.MealTypes.SelectMany(x => x.Items).Select(x => x.MenuItemId).Distinct().ToArray();
        var existingItemCount = await db.MealItems.CountAsync(x => itemIds.Contains(x.Id), ct);
        if (existingItemCount != itemIds.Length) throw NotFound("meal_not_found", "The selected meal does not exist.");

        var now = clock.GetUtcNow();
        var day = await db.MealPlanWeekdays.Include(x => x.DayItems).SingleOrDefaultAsync(x => x.MealPlanId == mealPlanId && x.DayOfWeek == dayOfWeek, ct);
        if (day is null)
        {
            day = new MealPlanWeekday { Id = Guid.NewGuid(), MealPlanId = mealPlanId, DayOfWeek = dayOfWeek, DisplayOrder = dayOfWeek + 1, CreatedAt = now, CreatedBy = userId };
            db.MealPlanWeekdays.Add(day);
        }
        day.IsActive = request.IsActive; day.UpdatedAt = now; day.UpdatedBy = userId;
        foreach (var row in day.DayItems) { row.IsActive = false; row.IsDefault = false; row.UpdatedAt = now; row.UpdatedBy = userId; }
        foreach (var type in request.MealTypes)
        foreach (var item in type.Items)
        {
            var row = day.DayItems.SingleOrDefault(x => x.MealTypeId == type.MealTypeId && x.MenuItemId == item.MenuItemId);
            if (row is null)
            {
                row = new MealPlanDayItem { Id = Guid.NewGuid(), MealTypeId = type.MealTypeId, MenuItemId = item.MenuItemId, CreatedAt = now, CreatedBy = userId };
                day.DayItems.Add(row);
            }
            row.IsDefault = item.IsDefault; row.DisplayOrder = item.DisplayOrder; row.IsActive = true; row.UpdatedAt = now; row.UpdatedBy = userId;
        }
        await db.SaveChangesAsync(ct);
    }

    private IQueryable<MealPlanWeekday> DayQuery(Guid mealPlanId) => db.MealPlanWeekdays.AsNoTracking()
        .Where(x => x.MealPlanId == mealPlanId)
        .Include(x => x.DayItems).ThenInclude(x => x.MealType)
        .Include(x => x.DayItems).ThenInclude(x => x.MenuItem).ThenInclude(x => x.Translations);

    private static WeeklyMenuDayResponse MapDay(MealPlanWeekday day) => new(day.DayOfWeek, DayName(day.DayOfWeek), day.IsActive,
        day.DayItems.Where(x => x.IsActive).GroupBy(x => new { x.MealTypeId, x.MealType.Code, x.MealType.DisplayOrder })
            .OrderBy(x => x.Key.DisplayOrder).Select(group => new WeeklyMenuMealTypeResponse(group.Key.MealTypeId, group.Key.Code,
                group.OrderBy(x => x.DisplayOrder).Select(item => new WeeklyMenuItemResponse(item.MenuItemId,
                    item.MenuItem.Translations.FirstOrDefault(t => t.LanguageCode == "en")?.Name ?? item.MenuItem.Sku,
                    item.IsDefault, item.DisplayOrder)).ToArray())).ToArray());
    private static string Name(MealPlanTemplate plan) => plan.Translations.FirstOrDefault(x => x.LanguageCode == "en")?.Name ?? plan.Code;
    private static string DayName(int day) => Enum.GetName((DayOfWeek)day) ?? day.ToString();
    private static void ValidateDay(int day) { if (day is < 0 or > 6) throw BadRequest("invalid_day_of_week", "DayOfWeek must be between 0 and 6."); }
    private static MealConfigurationException BadRequest(string code, string message) => new(400, code, message);
    private static MealConfigurationException NotFound(string code, string message) => new(404, code, message);
}
