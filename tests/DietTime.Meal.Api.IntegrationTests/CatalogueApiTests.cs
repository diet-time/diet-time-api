using DietTime.Domain;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using Testcontainers.PostgreSql;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed class CatalogueApiTests : IAsyncLifetime
{
    private readonly bool enabled = Environment.GetEnvironmentVariable("RUN_INTEGRATION_TESTS") == "true";
    private PostgreSqlContainer? postgres;
    private ApiFactory? factory; private HttpClient? client; private Guid planId; private Guid dayId; private Guid wednesdayDayId; private Guid mealId; private Guid oneDayPriceId; private Guid sixDayPriceId;
    public async Task InitializeAsync() { if (!enabled) return; postgres = new PostgreSqlBuilder().WithImage("postgres:16-alpine").WithDatabase("diettime_test").WithUsername("postgres").WithPassword("postgres").Build(); await postgres.StartAsync(); factory = new(postgres.GetConnectionString()); client = factory.CreateClient(); await SeedAsync(); }
    public async Task DisposeAsync() { client?.Dispose(); if (factory is not null) await factory.DisposeAsync(); if (postgres is not null) await postgres.DisposeAsync(); }

    [Fact] public async Task Meal_list_returns_only_configured_slot_options() { if (!enabled) return; var response = await client!.GetAsync($"/api/v1/meal-plans/{planId}/meals?templateDayId={dayId}"); Assert.True(response.IsSuccessStatusCode); Assert.Contains("DT-BRK-0001", await response.Content.ReadAsStringAsync()); }
    [Fact] public async Task Meal_list_filters_by_meal_type() { if (!enabled) return; Assert.True((await client!.GetAsync($"/api/v1/meal-plans/{planId}/meals?templateDayId={dayId}&mealType=BREAKFAST")).IsSuccessStatusCode); }
    [Fact] public async Task Meal_list_filters_by_plan_and_day() { if (!enabled) return; var body = await client!.GetStringAsync($"/api/v1/meal-plans/{planId}/meals?templateDayId={dayId}"); Assert.Contains(mealId.ToString(), body, StringComparison.OrdinalIgnoreCase); }
    [Fact] public async Task Meal_response_is_localized() { if (!enabled) return; var body = await client!.GetStringAsync($"/api/v1/meals/{mealId}?language=ar"); Assert.Contains("وجبة", body); }
    [Fact] public async Task Meal_details_returns_active_meal() { if (!enabled) return; Assert.True((await client!.GetAsync($"/api/v1/meals/{mealId}")).IsSuccessStatusCode); }
    [Fact] public async Task Missing_meal_returns_404() { if (!enabled) return; Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client!.GetAsync($"/api/v1/meals/{Guid.NewGuid()}")).StatusCode); }
    [Fact] public async Task Inactive_meal_is_excluded() { if (!enabled) return; using var scope = factory!.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DietTimeDbContext>(); var meal = await db.MealItems.FindAsync(mealId); meal!.Status = "INACTIVE"; await db.SaveChangesAsync(); Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client!.GetAsync($"/api/v1/meals/{mealId}")).StatusCode); }
    [Fact] public async Task Unpublished_plan_is_excluded() { if (!enabled) return; using var scope = factory!.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DietTimeDbContext>(); var plan = await db.MealPlanTemplates.FindAsync(planId); plan!.IsPublished = false; await db.SaveChangesAsync(); Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client!.GetAsync($"/api/v1/meal-plans/{planId}")).StatusCode); }
    [Fact] public async Task Template_days_are_ordered_and_use_uppercase_weekdays() { if (!enabled) return; var body = await client!.GetStringAsync($"/api/meal-plan-templates/{planId}/days"); Assert.True(body.IndexOf("WEDNESDAY", StringComparison.Ordinal) < body.IndexOf("THURSDAY", StringComparison.Ordinal)); Assert.DoesNotContain("dayNumber", body); }
    [Fact] public async Task Creating_weekday_menu_succeeds() { if (!enabled) return; var response = await client!.PostAsJsonAsync($"/api/meal-plan-templates/{planId}/days", new { menuWeekday = "SUNDAY", displayOrder = 2, isActive = true }); Assert.Equal(HttpStatusCode.Created, response.StatusCode); Assert.Contains("id", await response.Content.ReadAsStringAsync()); }
    [Fact] public async Task Creating_duplicate_weekday_returns_clear_conflict() { if (!enabled) return; var response = await client!.PostAsJsonAsync($"/api/meal-plan-templates/{planId}/days", new { menuWeekday = "WEDNESDAY", displayOrder = 5, isActive = true }); Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Contains("DUPLICATE_TEMPLATE_WEEKDAY", await response.Content.ReadAsStringAsync()); }
    [Fact] public async Task Wednesday_menu_contains_slots_and_options() { if (!enabled) return; var body = await client!.GetStringAsync($"/api/meal-plan-templates/{planId}/days/by-weekday/WEDNESDAY"); Assert.Contains(wednesdayDayId.ToString(), body, StringComparison.OrdinalIgnoreCase); Assert.Contains(mealId.ToString(), body, StringComparison.OrdinalIgnoreCase); Assert.Contains("minimumSelection", body); }
    [Fact] public async Task Deleting_template_day_soft_deactivates_without_deleting_slots() { if (!enabled) return; var response = await client!.DeleteAsync($"/api/meal-plan-templates/{planId}/days/{wednesdayDayId}"); Assert.Equal(HttpStatusCode.NoContent, response.StatusCode); using var scope = factory!.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DietTimeDbContext>(); var day = await db.MealPlanTemplateDays.Include(x => x.Slots).ThenInclude(x => x.Options).SingleAsync(x => x.Id == wednesdayDayId); Assert.False(day.IsActive); Assert.NotEmpty(day.Slots); Assert.NotEmpty(day.Slots.Single().Options); }
    [Fact] public async Task Updating_active_meal_creates_latest_draft_version() { if (!enabled) return; using var scope = factory!.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DietTimeDbContext>(); var source = await db.MealItems.Include(x => x.Translations).SingleAsync(x => x.Id == mealId); var request = new UpsertMealRequest(source.Sku, source.CategoryId, source.PreparationTimeMinutes, source.IsVegetarian, source.IsVegan, source.IsGlutenFree, source.IsDairyFree, source.IsAvailable, source.AvailableFrom, source.AvailableUntil, source.Translations.Select(x => new AdminTranslationRequest(x.LanguageCode, x.Name, x.ShortDescription, x.FullDescription)).ToArray(), null); var result = await scope.ServiceProvider.GetRequiredService<IAdminMealService>().UpdateMealAsync(mealId, request, null, default); Assert.NotNull(result); Assert.True(result.CreatedDraft); db.ChangeTracker.Clear(); var versions = await db.MealItems.Where(x => x.VersionGroupId == source.VersionGroupId).OrderBy(x => x.VersionNumber).ToListAsync(); Assert.Equal(2, versions.Count); Assert.Equal("ACTIVE", versions[0].Status); Assert.False(versions[0].IsLatest); Assert.Equal("DRAFT", versions[1].Status); Assert.True(versions[1].IsLatest); }
    [Fact]
    public async Task Activating_meal_version_repoints_current_plan_options_but_preserves_historical_plans()
    {
        if (!enabled) return;
        using var scope = factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DietTimeDbContext>();
        var now = DateTimeOffset.UtcNow;
        var source = await db.MealItems.SingleAsync(x => x.Id == mealId);
        source.IsLatest = false;

        var ingredient = new Ingredient
        {
            Code = $"ING-{Guid.NewGuid():N}",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
            RowVersion = 1
        };
        var draft = new MealItem
        {
            Id = Guid.NewGuid(),
            VersionGroupId = source.VersionGroupId,
            VersionNumber = source.VersionNumber + 1,
            IsLatest = true,
            SupersedesId = source.Id,
            Sku = source.Sku,
            CategoryId = source.CategoryId,
            Status = "DRAFT",
            IsAvailable = true,
            CreatedAt = now,
            UpdatedAt = now,
            RowVersion = 1,
            Translations =
            [
                new() { LanguageCode = "en", Name = "Updated meal", CreatedAt = now, UpdatedAt = now },
                new() { LanguageCode = "ar", Name = "Updated meal Arabic", CreatedAt = now, UpdatedAt = now }
            ],
            Nutrition = new() { CaloriesKcal = 425, CreatedAt = now, UpdatedAt = now },
            Ingredients =
            [
                new() { Ingredient = ingredient, IsPrimaryIngredient = true, DisplayOrder = 1, CreatedAt = now }
            ]
        };

        var mealType = await db.MealTypes.SingleAsync(x => x.Code == "BREAKFAST");
        var historicalPlanId = Guid.NewGuid();
        var historicalOptionId = Guid.NewGuid();
        var historicalPlan = new MealPlanTemplate
        {
            Id = historicalPlanId,
            VersionGroupId = historicalPlanId,
            Code = $"HISTORY-{Guid.NewGuid():N}",
            DurationDays = 1,
            IsLatest = false,
            IsPublished = false,
            IsActive = false,
            CreatedAt = now,
            UpdatedAt = now,
            RowVersion = 1
        };
        var historicalDay = new MealPlanTemplateDay
        {
            Plan = historicalPlan,
            MenuWeekday = MenuWeekday.Monday,
            IsActive = false,
            CreatedAt = now,
            UpdatedAt = now
        };
        var historicalSlot = new MealPlanTemplateSlot
        {
            Day = historicalDay,
            MealType = mealType,
            IsActive = false,
            CreatedAt = now,
            UpdatedAt = now,
            RowVersion = 1
        };
        historicalSlot.Options.Add(new()
        {
            Id = historicalOptionId,
            MealItemId = source.Id,
            IsAvailable = false,
            CreatedAt = now,
            UpdatedAt = now
        });

        db.Add(draft);
        db.Add(historicalPlan);
        db.MealMedia.Add(new()
        {
            EntityId = draft.Id,
            MediaType = MealMediaTypes.MealItem,
            ObjectKey = $"meal-items/{draft.Id:D}/images/meal.png",
            IsPrimary = true,
            Status = "ACTIVE",
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();

        var changed = await scope.ServiceProvider
            .GetRequiredService<IAdminMealService>()
            .SetMealStatusAsync(draft.Id, "ACTIVE", null, default);

        Assert.True(changed);
        db.ChangeTracker.Clear();
        Assert.Equal("ARCHIVED", (await db.MealItems.SingleAsync(x => x.Id == source.Id)).Status);
        Assert.Equal("ACTIVE", (await db.MealItems.SingleAsync(x => x.Id == draft.Id)).Status);
        Assert.All(
            await db.MealPlanSlotOptions
                .Where(x => x.Slot.Day.MealPlanTemplateId == planId)
                .ToListAsync(),
            option => Assert.Equal(draft.Id, option.MealItemId));
        Assert.Equal(
            source.Id,
            (await db.MealPlanSlotOptions.SingleAsync(x => x.Id == historicalOptionId)).MealItemId);
    }
    [Fact] public async Task Updating_published_template_creates_latest_draft_version() { if (!enabled) return; using var scope = factory!.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DietTimeDbContext>(); var source = await db.MealPlanTemplates.Include(x => x.Translations).SingleAsync(x => x.Id == planId); var request = new CreatePlanRequest(source.Code, source.PlanType, source.DurationDays, source.IsCustomizable, source.ValidFrom, source.ValidUntil, source.Translations.Select(x => new AdminTranslationRequest(x.LanguageCode, x.Name, x.ShortDescription, x.FullDescription)).ToArray()); var result = await scope.ServiceProvider.GetRequiredService<IAdminMealService>().UpdatePlanAsync(planId, request, null, default); Assert.NotNull(result); Assert.True(result.CreatedDraft); db.ChangeTracker.Clear(); var versions = await db.MealPlanTemplates.Include(x => x.Days).Where(x => x.VersionGroupId == source.VersionGroupId).OrderBy(x => x.VersionNumber).ToListAsync(); Assert.Equal(2, versions.Count); Assert.True(versions[0].IsPublished); Assert.False(versions[0].IsLatest); Assert.False(versions[1].IsPublished); Assert.True(versions[1].IsLatest); Assert.NotEmpty(versions[1].Days); }
    [Fact] public async Task Admin_meal_list_returns_translations_availability_price_category_and_nutrition() { if (!enabled) return; using var scope = factory!.Services.CreateScope(); var result = await scope.ServiceProvider.GetRequiredService<IAdminMealService>().GetMealsAsync(null, 1, 25, default); var meal = Assert.Single(result.Items); Assert.Equal("وجبة تجريبية", meal.NameAr); Assert.True(meal.IsAvailable); Assert.NotNull(meal.AvailableFrom); Assert.NotNull(meal.AvailableUntil); Assert.Equal("BREAKFAST", meal.Category.Code); Assert.Equal("Breakfast", meal.Category.Name); Assert.Equal(500, meal.Nutrition?.ServingQuantity); Assert.Equal(420, meal.Nutrition?.CaloriesKcal); Assert.Equal(25, meal.Price?.Amount); Assert.Equal("QAR", meal.Price?.CurrencyCode); }
    [Fact] public async Task Guest_home_is_public_and_returns_plans_slots_and_dates_without_meals() { if (!enabled) return; var today = DateOnly.FromDateTime(DateTime.UtcNow); var response = await client!.GetAsync("/api/v1/guest/home?date=2026-07-23"); Assert.Equal(HttpStatusCode.OK, response.StatusCode); var body = await response.Content.ReadAsStringAsync(); using var json = System.Text.Json.JsonDocument.Parse(body); var data = json.RootElement.GetProperty("data"); Assert.Equal(2, data.GetProperty("mealPlans").GetArrayLength()); var selectedPlan = data.GetProperty("mealPlans").EnumerateArray().Single(x => x.GetProperty("isSelected").GetBoolean()); Assert.Equal("CLASSIC", selectedPlan.GetProperty("code").GetString()); Assert.Equal("Classic short description", selectedPlan.GetProperty("description").GetString()); Assert.Equal("https://cdn.test/plan.png", selectedPlan.GetProperty("imageUrl").GetString()); var slots = selectedPlan.GetProperty("slots").EnumerateArray().ToArray(); Assert.Equal(["BREAKFAST", "SNACK_DESSERT"], slots.Select(x => x.GetProperty("mealTime").GetProperty("code").GetString()).ToArray()); Assert.All(slots, slot => Assert.False(slot.TryGetProperty("meals", out _))); var calendar = data.GetProperty("weeklyCalendar").EnumerateArray().ToArray(); Assert.Equal(7, calendar.Length); Assert.Equal(today.AddDays(2), DateOnly.Parse(calendar[0].GetProperty("date").GetString()!)); Assert.False(data.TryGetProperty("menus", out _)); Assert.False(data.TryGetProperty("pagination", out _)); }
    [Fact] public async Task Meal_plan_categories_return_daily_price_from_the_preferred_active_package() { if (!enabled) return; using var json = System.Text.Json.JsonDocument.Parse(await client!.GetStringAsync("/api/v1/meal-plan-categories?language=en")); var plans = json.RootElement.GetProperty("data").EnumerateArray().ToArray(); Assert.Equal(2, plans.Length); var classic = plans.Single(x => x.GetProperty("code").GetString() == "CLASSIC"); Assert.Equal("https://cdn.test/plan.png", classic.GetProperty("imageUrl").GetString()); Assert.Equal(420, classic.GetProperty("dailyCaloriesKcal").GetDecimal()); Assert.Equal(55, classic.GetProperty("startingPrice").GetDecimal()); Assert.Equal(55, classic.GetProperty("displayDailyPrice").GetDecimal()); Assert.Equal("QAR", classic.GetProperty("currencyCode").GetString()); Assert.Equal(1, classic.GetProperty("sourceDurationDays").GetInt32()); Assert.Equal(oneDayPriceId, classic.GetProperty("pricingRecordId").GetGuid()); Assert.True(classic.GetProperty("hasActivePrice").GetBoolean()); var premium = plans.Single(x => x.GetProperty("code").GetString() == "PREMIUM"); Assert.Equal(300, premium.GetProperty("startingPrice").GetDecimal()); Assert.Equal(50, premium.GetProperty("displayDailyPrice").GetDecimal()); Assert.Equal(6, premium.GetProperty("sourceDurationDays").GetInt32()); Assert.Equal(sixDayPriceId, premium.GetProperty("pricingRecordId").GetGuid()); }
    [Fact] public async Task Guest_home_localizes_plans_and_falls_back_to_english() { if (!enabled) return; var body = await client!.GetStringAsync("/api/v1/guest/home?date=2026-07-23&language=ar"); using var json = System.Text.Json.JsonDocument.Parse(body); var plans = json.RootElement.GetProperty("data").GetProperty("mealPlans").EnumerateArray().ToArray(); var classic = plans.Single(x => x.GetProperty("code").GetString() == "CLASSIC"); var premium = plans.Single(x => x.GetProperty("code").GetString() == "PREMIUM"); Assert.Equal("Arabic Classic", classic.GetProperty("name").GetString()); Assert.Equal("Arabic plan short description", classic.GetProperty("description").GetString()); Assert.Equal("Premium", premium.GetProperty("name").GetString()); }
    [Fact] public async Task Guest_home_selects_requested_plan() { if (!enabled) return; var body = await client!.GetStringAsync("/api/v1/guest/home?date=2026-07-23&planCode=premium"); using var json = System.Text.Json.JsonDocument.Parse(body); var selected = json.RootElement.GetProperty("data").GetProperty("mealPlans").EnumerateArray().Single(x => x.GetProperty("isSelected").GetBoolean()); Assert.Equal("PREMIUM", selected.GetProperty("code").GetString()); }
    [Fact] public async Task Guest_menu_returns_meals_for_specific_plan_and_date() { if (!enabled) return; var response = await client!.GetAsync("/api/v1/guest/meal-plans/CLASSIC/menu?date=2026-07-23"); Assert.Equal(HttpStatusCode.OK, response.StatusCode); var body = await response.Content.ReadAsStringAsync(); using var json = System.Text.Json.JsonDocument.Parse(body); var data = json.RootElement.GetProperty("data"); Assert.Equal("CLASSIC", data.GetProperty("planCode").GetString()); Assert.Equal("2026-07-23", data.GetProperty("date").GetString()); var slot = Assert.Single(data.GetProperty("slots").EnumerateArray()); var meal = Assert.Single(slot.GetProperty("meals").EnumerateArray()); Assert.Equal("DT-BRK-0001", meal.GetProperty("code").GetString()); }
    [Fact] public async Task Guest_allergens_are_public_active_localized_and_cache_is_invalidated_on_update() { if (!enabled) return; var response = await client!.GetAsync("/api/v1/guest/allergens?language=ar"); Assert.Equal(HttpStatusCode.OK, response.StatusCode); using (var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync())) { var allergen = Assert.Single(json.RootElement.GetProperty("data").EnumerateArray()); Assert.Equal("TREE_NUTS", allergen.GetProperty("code").GetString()); Assert.Equal("Tree nuts Arabic", allergen.GetProperty("name").GetString()); Assert.NotEqual(Guid.Empty, allergen.GetProperty("id").GetGuid()); } using var scope = factory!.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DietTimeDbContext>(); var translation = await db.AllergenTranslations.SingleAsync(x => x.LanguageCode == "ar"); translation.Name = "Updated Arabic name"; await db.SaveChangesAsync(); using var updated = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync("/api/v1/guest/allergens?language=ar")); Assert.Equal("Updated Arabic name", updated.RootElement.GetProperty("data")[0].GetProperty("name").GetString()); }
    [Theory] [InlineData("/api/v1/guest/home?language=fr")] [InlineData("/api/v1/guest/home?planCode=not%20valid")] [InlineData("/api/v1/guest/meal-plans/CLASSIC/menu")] [InlineData("/api/v1/guest/meal-plans/CLASSIC/menu?date=2026-07-23&language=fr")] [InlineData("/api/v1/guest/allergens?language=fr")] public async Task Guest_endpoints_reject_invalid_parameters(string path) { if (!enabled) return; Assert.Equal(HttpStatusCode.BadRequest, (await client!.GetAsync(path)).StatusCode); }
    [Theory] [InlineData("/api/v1/guest/home?date=2026-07-24")] [InlineData("/api/v1/guest/meal-plans/CLASSIC/menu?date=2026-07-24")] [InlineData("/api/v1/guest/meal-plans/UNKNOWN/menu?date=2026-07-23")] public async Task Guest_endpoints_return_404_when_menu_does_not_exist(string path) { if (!enabled) return; Assert.Equal(HttpStatusCode.NotFound, (await client!.GetAsync(path)).StatusCode); }

    private async Task SeedAsync()
    {
        using var scope = factory!.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DietTimeDbContext>(); await db.Database.EnsureCreatedAsync(); var now = DateTimeOffset.UtcNow;
        var category = new MealCategory { Code = "BREAKFAST", IsActive = true, CreatedAt = now, UpdatedAt = now, RowVersion = 1, Translations = [new() { LanguageCode = "en", Name = "Breakfast", CreatedAt = now, UpdatedAt = now }] };
        var allergen = new Allergen { Code = "TREE_NUTS", IsActive = true, CreatedAt = now, UpdatedAt = now, Translations = [new() { LanguageCode = "en", Name = "Tree nuts", CreatedAt = now, UpdatedAt = now }, new() { LanguageCode = "ar", Name = "Tree nuts Arabic", CreatedAt = now, UpdatedAt = now }] };
        var inactiveAllergen = new Allergen { Code = "INACTIVE", IsActive = false, CreatedAt = now, UpdatedAt = now, Translations = [new() { LanguageCode = "en", Name = "Inactive", CreatedAt = now, UpdatedAt = now }] };
        var type = new MealType { Code = "BREAKFAST", IsActive = true, DisplayOrder = 1, CreatedAt = now, UpdatedAt = now, Translations = [new() { LanguageCode = "en", Name = "Breakfast", CreatedAt = now, UpdatedAt = now }, new() { LanguageCode = "ar", Name = "الإفطار", CreatedAt = now, UpdatedAt = now }] };
        var snackType = new MealType { Code = "SNACK_DESSERT", IsActive = true, DisplayOrder = 4, CreatedAt = now, UpdatedAt = now, Translations = [new() { LanguageCode = "en", Name = "Snack / Dessert", CreatedAt = now, UpdatedAt = now }] };
        var meal = new MealItem { Sku = "DT-BRK-0001", Category = category, Status = "ACTIVE", IsAvailable = true, AvailableFrom = now.AddDays(-1), AvailableUntil = now.AddDays(2), CreatedAt = now, UpdatedAt = now, RowVersion = 1, Translations = [new() { LanguageCode = "en", Name = "Test Meal", CreatedAt = now, UpdatedAt = now }, new() { LanguageCode = "ar", Name = "وجبة تجريبية", CreatedAt = now, UpdatedAt = now }], Nutrition = new() { ServingQuantity = 500, ServingUnit = "g", CaloriesKcal = 420, ProteinGrams = 35, CarbohydratesGrams = 40, FatGrams = 12, CreatedAt = now, UpdatedAt = now }, Prices = [new() { PriceType = "INDIVIDUAL", CurrencyCode = "QAR", Amount = 25, EffectiveFrom = now.AddDays(-1), IsActive = true, CreatedAt = now, UpdatedAt = now }] }; mealId = meal.Id = Guid.NewGuid(); meal.VersionGroupId = mealId;
        var plan = new MealPlanTemplate { Code = "CLASSIC", PlanType = "STANDARD", DurationDays = 7, IsActive = true, IsPublished = true, IsCustomizable = true, CreatedAt = now, UpdatedAt = now, RowVersion = 1, Translations = [new() { LanguageCode = "en", Name = "Classic", ShortDescription = "Classic short description", CreatedAt = now, UpdatedAt = now }, new() { LanguageCode = "ar", Name = "Arabic Classic", ShortDescription = "Arabic plan short description", CreatedAt = now, UpdatedAt = now }] }; planId = plan.Id = Guid.NewGuid(); plan.VersionGroupId = planId;
        oneDayPriceId = Guid.NewGuid();
        plan.Prices.Add(new() { Id = oneDayPriceId, DurationDays = 1, MealsPerDay = 1, SnacksPerDay = 0, CurrencyCode = "QAR", Amount = 55, EffectiveFrom = now.AddDays(-1), IsActive = true, CreatedAt = now, UpdatedAt = now });
        plan.Prices.Add(new() { DurationDays = 6, MealsPerDay = 1, SnacksPerDay = 0, CurrencyCode = "QAR", Amount = 300, EffectiveFrom = now.AddDays(-1), IsActive = true, CreatedAt = now, UpdatedAt = now });
        plan.Prices.Add(new() { DurationDays = 1, MealsPerDay = 1, SnacksPerDay = 0, CurrencyCode = "QAR", Amount = 1, EffectiveFrom = now.AddDays(1), IsActive = true, CreatedAt = now, UpdatedAt = now });
        plan.Prices.Add(new() { DurationDays = 1, MealsPerDay = 1, SnacksPerDay = 0, CurrencyCode = "QAR", Amount = 2, EffectiveFrom = now.AddDays(-2), EffectiveUntil = now.AddDays(-1), IsActive = true, CreatedAt = now, UpdatedAt = now });
        plan.Prices.Add(new() { DurationDays = 1, MealsPerDay = 1, SnacksPerDay = 0, CurrencyCode = "QAR", Amount = 3, EffectiveFrom = now.AddDays(-1), IsActive = false, CreatedAt = now, UpdatedAt = now });
        var day = new MealPlanTemplateDay { Plan = plan, MenuWeekday = MenuWeekday.Thursday, DisplayOrder = 6, IsActive = true, CreatedAt = now, UpdatedAt = now }; dayId = day.Id = Guid.NewGuid(); var slot = new MealPlanTemplateSlot { Day = day, MealType = type, DisplayOrder = 2, MinimumSelection = 1, MaximumSelection = 1, IsRequired = true, AllowsPaidUpgrade = true, IsActive = true, CreatedAt = now, UpdatedAt = now, RowVersion = 1 }; slot.Options.Add(new() { MealItem = meal, IsAvailable = true, CreatedAt = now, UpdatedAt = now }); day.Slots.Add(new() { MealType = snackType, DisplayOrder = 1, MinimumSelection = 0, MaximumSelection = 1, IsRequired = false, AllowsPaidUpgrade = true, IsActive = true, CreatedAt = now, UpdatedAt = now, RowVersion = 1 });
        var wednesday = new MealPlanTemplateDay { Plan = plan, MenuWeekday = MenuWeekday.Wednesday, DisplayOrder = 5, IsActive = true, CreatedAt = now, UpdatedAt = now }; wednesdayDayId = wednesday.Id = Guid.NewGuid(); var wednesdaySlot = new MealPlanTemplateSlot { Day = wednesday, MealType = type, DisplayOrder = 1, MinimumSelection = 1, MaximumSelection = 1, IsRequired = true, AllowsPaidUpgrade = true, IsActive = true, CreatedAt = now, UpdatedAt = now, RowVersion = 1 }; wednesdaySlot.Options.Add(new() { MealItem = meal, IsAvailable = true, IsDefault = true, CreatedAt = now, UpdatedAt = now });
        var secondPlan = new MealPlanTemplate { Code = "PREMIUM", PlanType = "STANDARD", DurationDays = 7, IsActive = true, IsPublished = true, IsCustomizable = true, CreatedAt = now, UpdatedAt = now, RowVersion = 1, Translations = [new() { LanguageCode = "en", Name = "Premium", ShortDescription = "Premium short description", CreatedAt = now, UpdatedAt = now }] }; secondPlan.Id = Guid.NewGuid(); secondPlan.VersionGroupId = secondPlan.Id;
        sixDayPriceId = Guid.NewGuid();
        secondPlan.Prices.Add(new() { Id = sixDayPriceId, DurationDays = 6, MealsPerDay = 1, SnacksPerDay = 0, CurrencyCode = "BHD", Amount = 300, EffectiveFrom = now.AddDays(-1), IsActive = true, CreatedAt = now, UpdatedAt = now });
        var secondDay = new MealPlanTemplateDay { Plan = secondPlan, MenuWeekday = MenuWeekday.Thursday, DisplayOrder = 6, IsActive = true, CreatedAt = now, UpdatedAt = now }; var secondSlot = new MealPlanTemplateSlot { Day = secondDay, MealType = type, MinimumSelection = 1, MaximumSelection = 1, IsRequired = true, AllowsPaidUpgrade = true, IsActive = true, CreatedAt = now, UpdatedAt = now, RowVersion = 1 }; secondSlot.Options.Add(new() { MealItem = meal, IsAvailable = true, CreatedAt = now, UpdatedAt = now });
        db.Add(plan); db.Add(secondPlan); db.Add(allergen); db.Add(inactiveAllergen); db.MealMedia.Add(new() { EntityId = planId, MediaType = MealMediaTypes.MealPlan, ObjectKey = $"meal-plans/{planId:D}/images/plan.png", PublicUrl = "https://cdn.test/plan.png", IsPrimary = true, Status = "ACTIVE", CreatedAt = now, UpdatedAt = now }); await db.SaveChangesAsync();
    }
}

internal sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = connectionString, ["Jwt:Issuer"] = "DietTime.Tests", ["Jwt:Audience"] = "DietTime.Tests", ["Jwt:Key"] = "test-only-key-at-least-thirty-two-characters-long", ["Storage:PublicBaseUrl"] = "https://cdn.test", ["Storage:BucketName"] = "test", ["Storage:ServiceUrl"] = "http://localhost:9000", ["Storage:AccessKey"] = "test", ["Storage:SecretKey"] = "test" }));
}
