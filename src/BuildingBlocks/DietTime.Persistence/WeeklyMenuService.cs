using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Persistence;

public sealed class WeeklyMenuService(DietTimeDbContext db, TimeProvider clock) : IWeeklyMenuService
{
    public async Task<WeeklyMenuResponse> GetAsync(Guid mealPlanId, CancellationToken ct)
    {
        var plan = await db.MealPlanTemplates.AsNoTracking().Include(x => x.Translations)
            .SingleOrDefaultAsync(x => x.Id == mealPlanId, ct)
            ?? throw NotFound("meal_plan_not_found", "The meal plan does not exist.");
        var days = await DayQuery(mealPlanId).ToListAsync(ct);
        return new(mealPlanId, Name(plan), days.OrderBy(x => ToDayOfWeek(x.MenuWeekday)).Select(MapDay).ToArray());
    }

    public async Task<WeeklyMenuDayResponse> GetDayAsync(Guid mealPlanId, int dayOfWeek, CancellationToken ct)
    {
        var weekday = ToMenuWeekday(dayOfWeek);
        if (!await db.MealPlanTemplates.AnyAsync(x => x.Id == mealPlanId, ct))
            throw NotFound("meal_plan_not_found", "The meal plan does not exist.");
        var day = await DayQuery(mealPlanId).SingleOrDefaultAsync(x => x.MenuWeekday == weekday, ct)
            ?? throw NotFound("weekly_menu_day_not_found", $"No {DayName(dayOfWeek)} menu is configured for this meal plan.");
        return MapDay(day);
    }

    public async Task UpdateDayAsync(Guid mealPlanId, int dayOfWeek, UpdateWeeklyMenuDayRequest request, Guid? userId, CancellationToken ct)
    {
        var weekday = ToMenuWeekday(dayOfWeek);
        if (!await db.MealPlanTemplates.AnyAsync(x => x.Id == mealPlanId, ct))
            throw NotFound("meal_plan_not_found", "The meal plan does not exist.");
        if (request.MealTypes is null) throw BadRequest("meal_types_required", "Meal types are required.");
        if (request.MealTypes.GroupBy(x => x.MealTypeId).Any(x => x.Count() > 1))
            throw BadRequest("duplicate_meal_type", "A meal type can only be configured once per weekday.");
        foreach (var type in request.MealTypes)
        {
            if (type.Items is null) throw BadRequest("items_required", "Menu items are required.");
            if (type.Items.GroupBy(x => x.MenuItemId).Any(x => x.Count() > 1))
                throw BadRequest("duplicate_menu_item", "A meal cannot be configured more than once for the same weekday and meal type.");
            if (type.Items.Count(x => x.IsDefault) > 1)
                throw BadRequest("multiple_default_meals", $"Only one default meal can be configured for the selected meal type on {DayName(dayOfWeek)}.");
        }

        var typeIds = request.MealTypes.Select(x => x.MealTypeId).Distinct().ToArray();
        var types = await db.MealTypes.Where(x => typeIds.Contains(x.Id)).ToListAsync(ct);
        if (types.Count != typeIds.Length) throw NotFound("meal_type_not_found", "The selected meal type does not exist.");
        if (types.Any(x => !x.IsActive)) throw BadRequest("inactive_meal_type", "The selected meal type is inactive.");
        var itemIds = request.MealTypes.SelectMany(x => x.Items).Select(x => x.MenuItemId).Distinct().ToArray();
        if (await db.MealItems.CountAsync(x => itemIds.Contains(x.Id), ct) != itemIds.Length)
            throw NotFound("meal_not_found", "The selected meal does not exist.");

        var now = clock.GetUtcNow();
        var day = await db.MealPlanTemplateDays.Include(x => x.Slots).ThenInclude(x => x.Options)
            .SingleOrDefaultAsync(x => x.MealPlanTemplateId == mealPlanId && x.MenuWeekday == weekday, ct);
        if (day is null)
        {
            var desiredOrder = dayOfWeek + 1;
            var usedOrders = await db.MealPlanTemplateDays.AsNoTracking()
                .Where(x => x.MealPlanTemplateId == mealPlanId).Select(x => x.DisplayOrder).ToListAsync(ct);
            var displayOrder = usedOrders.Contains(desiredOrder) ? usedOrders.DefaultIfEmpty().Max() + 1 : desiredOrder;
            day = new MealPlanTemplateDay
            {
                Id = Guid.NewGuid(), MealPlanTemplateId = mealPlanId, MenuWeekday = weekday,
                DisplayOrder = displayOrder, CreatedAt = now, CreatedBy = userId
            };
            db.MealPlanTemplateDays.Add(day);
        }
        day.IsActive = request.IsActive;
        day.UpdatedAt = now;
        day.UpdatedBy = userId;

        foreach (var slot in day.Slots)
        {
            slot.IsActive = false;
            slot.UpdatedAt = now;
            slot.UpdatedBy = userId;
            slot.RowVersion++;
            foreach (var option in slot.Options)
            {
                option.IsAvailable = false;
                option.IsDefault = false;
                option.UpdatedAt = now;
                option.UpdatedBy = userId;
            }
        }

        foreach (var requestedType in request.MealTypes)
        {
            var mealType = types.Single(x => x.Id == requestedType.MealTypeId);
            var slot = day.Slots.SingleOrDefault(x => x.MealTypeId == requestedType.MealTypeId);
            if (slot is null)
            {
                slot = new MealPlanTemplateSlot
                {
                    Id = Guid.NewGuid(), MealTypeId = requestedType.MealTypeId,
                    DisplayOrder = mealType.DisplayOrder, MinimumSelection = 0, MaximumSelection = 1,
                    IsRequired = false, AllowsPaidUpgrade = true, CreatedAt = now, CreatedBy = userId, RowVersion = 1
                };
                day.Slots.Add(slot);
            }
            slot.IsActive = true;
            slot.UpdatedAt = now;
            slot.UpdatedBy = userId;

            foreach (var requestedItem in requestedType.Items)
            {
                var option = slot.Options.SingleOrDefault(x => x.MealItemId == requestedItem.MenuItemId);
                if (option is null)
                {
                    option = new MealPlanSlotOption
                    {
                        Id = Guid.NewGuid(), MealItemId = requestedItem.MenuItemId,
                        AdditionalPrice = 0, CreatedAt = now, CreatedBy = userId
                    };
                    slot.Options.Add(option);
                }
                option.IsDefault = requestedItem.IsDefault;
                option.DisplayOrder = requestedItem.DisplayOrder;
                option.IsAvailable = true;
                option.UpdatedAt = now;
                option.UpdatedBy = userId;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private IQueryable<MealPlanTemplateDay> DayQuery(Guid mealPlanId) => db.MealPlanTemplateDays.AsNoTracking()
        .Where(x => x.MealPlanTemplateId == mealPlanId)
        .Include(x => x.Slots).ThenInclude(x => x.MealType)
        .Include(x => x.Slots).ThenInclude(x => x.Options).ThenInclude(x => x.MealItem).ThenInclude(x => x.Translations);

    private static WeeklyMenuDayResponse MapDay(MealPlanTemplateDay day)
    {
        var dayOfWeek = ToDayOfWeek(day.MenuWeekday);
        return new(dayOfWeek, DayName(dayOfWeek), day.IsActive,
            day.Slots.Where(x => x.IsActive).OrderBy(x => x.DisplayOrder)
                .Select(slot => new WeeklyMenuMealTypeResponse(slot.MealTypeId, slot.MealType.Code,
                    slot.Options.Where(x => x.IsAvailable).OrderBy(x => x.DisplayOrder)
                        .Select(option => new WeeklyMenuItemResponse(option.MealItemId,
                            option.MealItem.Translations.FirstOrDefault(t => t.LanguageCode == "en")?.Name ?? option.MealItem.Sku,
                            option.IsDefault, option.DisplayOrder)).ToArray())).ToArray());
    }

    private static MenuWeekday ToMenuWeekday(int day) => day switch
    {
        0 => MenuWeekday.Sunday, 1 => MenuWeekday.Monday, 2 => MenuWeekday.Tuesday,
        3 => MenuWeekday.Wednesday, 4 => MenuWeekday.Thursday, 5 => MenuWeekday.Friday,
        6 => MenuWeekday.Saturday,
        _ => throw BadRequest("invalid_day_of_week", "DayOfWeek must be between 0 and 6.")
    };
    private static int ToDayOfWeek(MenuWeekday day) => day switch
    {
        MenuWeekday.Sunday => 0, MenuWeekday.Monday => 1, MenuWeekday.Tuesday => 2,
        MenuWeekday.Wednesday => 3, MenuWeekday.Thursday => 4, MenuWeekday.Friday => 5,
        MenuWeekday.Saturday => 6, _ => throw new ArgumentOutOfRangeException(nameof(day))
    };
    private static string Name(MealPlanTemplate plan) => plan.Translations.FirstOrDefault(x => x.LanguageCode == "en")?.Name ?? plan.Code;
    private static string DayName(int day) => Enum.GetName((DayOfWeek)day) ?? day.ToString();
    private static MealConfigurationException BadRequest(string code, string message) => new(400, code, message);
    private static MealConfigurationException NotFound(string code, string message) => new(404, code, message);
}
