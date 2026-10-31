using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Tomorrow_dishes_retain_localized_meal_types_and_configured_sort_order()
    {
        var initial = await PrepareAsync();
        var existing = await db.MealPlanTemplateSlots.Include(s => s.MealType).Include(s => s.Options)
            .FirstAsync(s => s.Day.WeekNumber == 1 && s.Day.MenuWeekday == MenuWeekday.Tuesday && s.MealType.Code == "MEAL");
        existing.MealType.DisplayOrder = 50;
        var type = new MealType { Id = Guid.NewGuid(), Code = "CHOOSER_TEST", DisplayOrder = -10, IsActive = true };
        type.Translations.Add(new MealTypeTranslation { LanguageCode = "en", Name = "Late meal" });
        type.Translations.Add(new MealTypeTranslation { LanguageCode = "ar", Name = "وجبة مسائية" });
        var sharedDish = existing.Options.First().MealItemId;
        var slot = new MealPlanTemplateSlot
        {
            Id = Guid.NewGuid(), MealPlanTemplateDayId = existing.MealPlanTemplateDayId,
            MealType = type, IsActive = true, MaximumSelection = 5
        };
        slot.Options.Add(new MealPlanSlotOption
        {
            Id = Guid.NewGuid(), MealItemId = sharedDish, IsAvailable = true
        });
        db.Add(slot);
        await db.SaveChangesAsync();
        var menu = (await Service().GetTomorrowAsync(order.Id, userId, "en", default))!;
        var dish = Assert.Single(menu.AvailableMeals, m => m.MealItemId == sharedDish);
        Assert.Equal(initial.AvailableMeals.Count, menu.AvailableMeals.Count);
        Assert.Equal(new[] { -10, 50 }, dish.MealTypes.Select(t => t.DisplayOrder));
        Assert.Equal("Late meal", dish.MealTypes[0].Name);
        Assert.Equal(type.Id, dish.MealTypes[0].Id);
        Assert.NotEmpty(menu.SelectedMeals);
        Assert.All(menu.SelectedMeals, m => Assert.NotEmpty(m.MealTypes));
        var arabic = (await Service().GetTomorrowAsync(order.Id, userId, "ar", default))!;
        Assert.Equal("وجبة مسائية", Assert.Single(arabic.AvailableMeals, m => m.MealItemId == sharedDish).MealTypes[0].Name);
        type.DisplayOrder = 100;
        await db.SaveChangesAsync();
        var reordered = (await Service().GetTomorrowAsync(order.Id, userId, "en", default))!;
        Assert.Equal(new[] { 50, 100 }, Assert.Single(reordered.AvailableMeals, m => m.MealItemId == sharedDish).MealTypes.Select(t => t.DisplayOrder));
    }
}