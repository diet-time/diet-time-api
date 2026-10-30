using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Assigned_and_available_nutrition_uses_actual_serving_and_survives_confirmation()
    {
        var dishes = await db.MealItems.Include(m => m.Translations).OrderBy(m => m.Sku).ToArrayAsync();
        foreach (var dish in dishes.Take(6))
        {
            dish.Nutrition = new MealNutrition { Id = Guid.NewGuid(), MealItemId = dish.Id,
                ServingQuantity = 250, ServingUnit = "g", CaloriesKcal = 320,
                ProteinGrams = 24, CarbohydratesGrams = 40, FatGrams = 0, FiberGrams = 3,
                SodiumMg = 150, CreatedAt = clock.Now, UpdatedAt = clock.Now };
            db.Add(dish.Nutrition);
        }
        var arabicText = new MealItemTranslation { Id = Guid.NewGuid(), MealItemId = dishes[0].Id, LanguageCode = "ar", Name = "دجاج مشوي" };
        dishes[0].Translations.Single(t => t.LanguageCode == "en").ShortDescription = "English description";
        db.Add(arabicText);
        await db.SaveChangesAsync();
        var menu = await PrepareAsync();
        Assert.Contains(menu.AvailableSnacks, m => m.Nutrition == null && m.Calories == null);
        var item = menu.SelectedMeals.First(m => m.Nutrition != null);
        Assert.Equal(1, item.Quantity); Assert.Equal(250, item.Nutrition!.ServingQuantity);
        Assert.Equal(320, item.Nutrition.CaloriesKcal); Assert.Equal(24, item.Nutrition.ProteinGrams);
        Assert.Equal(0, item.Nutrition.FatGrams); Assert.Equal(320, item.Calories);
        var arabic = (await Service().GetTomorrowAsync(order.Id, userId, "ar", default))!;
        Assert.Contains(arabic.AvailableMeals, m => m.MealItemId == dishes[0].Id && m.MealName == "دجاج مشوي");
        Assert.Equal("English description", arabic.AvailableMeals.Single(m => m.MealItemId == dishes[0].Id).Description);
        Assert.Contains(arabic.AvailableMeals, m => m.MealItemId == dishes[1].Id && m.MealName == dishes[1].Translations.Single().Name);
        var confirmed = await Service().ConfirmAsync(order.Id, menu.SubscriptionServiceDayId!.Value, userId, "en", Request(menu), default);
        Assert.True(confirmed.SelectionLocked); Assert.Empty(confirmed.AvailableMeals);
        Assert.All(confirmed.SelectedMeals.Where(m => m.Nutrition != null), m => Assert.Equal(320, m.Nutrition!.CaloriesKcal));
        var details = (await Service().GetMenuAsync(order.Id, menu.SubscriptionServiceDayId.Value, userId, "en", default))!;
        Assert.Equal(confirmed.SelectedMenuItemIds.Order(), details.SelectedMeals.Select(m => m.Meal.MealItemId).Order());
        Assert.All(details.SelectedMeals.Where(m => m.Meal.Nutrition != null), m => Assert.Equal(250, m.Meal.Nutrition!.ServingQuantity));
    }

    [UpcomingPostgresFact]
    public async Task Language_patch_updates_only_owned_existing_preference_and_preserves_questionnaire_and_orders()
    {
        var setup = await ApprovalClient(); await using var factory = setup.Factory; using var client = setup.Client;
        var before = await db.CustomerProfiles.AsNoTracking().SingleAsync();
        var response = await client.PatchAsJsonAsync("/api/v1/customer/profile/preferred-language", new UpdateCustomerLanguageRequest("ar"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var after = await db.CustomerProfiles.AsNoTracking().SingleAsync();
        Assert.Equal("ar", after.PreferredLanguage); Assert.Equal(before.OnboardingStatus, after.OnboardingStatus);
        Assert.Equal(before.WeightKg, after.WeightKg); Assert.Equal(before.GoalCode, after.GoalCode);
        Assert.Equal(before.RowVersion + 1, after.RowVersion);
        Assert.Equal("CONFIRMED", (await db.Orders.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsJsonAsync("/api/v1/customer/profile/preferred-language", new UpdateCustomerLanguageRequest("fr"))).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Development-User-Id");
        client.DefaultRequestHeaders.Add("X-Development-User-Id", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync("/api/v1/customer/profile/preferred-language", new UpdateCustomerLanguageRequest("en"))).StatusCode);
        Assert.Equal("ar", (await db.CustomerProfiles.AsNoTracking().SingleAsync()).PreferredLanguage);
    }
}
