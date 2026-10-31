using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    private async Task<MealPlanTemplate> SeedBrowsingCycle(string code, DateOnly start)
    {
        var type = new MealType { Id = Guid.NewGuid(), Code = code + "_TYPE", DisplayOrder = 10, IsActive = true,
            Translations = [new MealTypeTranslation { Id = Guid.NewGuid(), LanguageCode = "en", Name = "AM Meal" }, new MealTypeTranslation { Id = Guid.NewGuid(), LanguageCode = "ar", Name = "وجبة صباحية" }] };
        var category = await db.MealCategories.FirstAsync();
        var plan = new MealPlanTemplate { Id = Guid.NewGuid(), VersionGroupId = Guid.NewGuid(), Code = code, CycleWeeks = 4,
            ValidFrom = start, IsActive = true, IsPublished = true,
            Translations = [new MealPlanTemplateTranslation { Id = Guid.NewGuid(), LanguageCode = "en", Name = code }] };
        for (var week = 1; week <= 4; week++)
        foreach (var weekday in Enum.GetValues<MenuWeekday>())
        {
            if (week == 3 && weekday == MenuWeekday.Wednesday) continue;
            var day = new MealPlanTemplateDay { Id = Guid.NewGuid(), WeekNumber = week, MenuWeekday = weekday, DisplayOrder = (int)weekday + 1, IsActive = true };
            var slot = new MealPlanTemplateSlot { Id = Guid.NewGuid(), MealType = type, IsActive = true, DisplayOrder = 1, MinimumSelection = 1, MaximumSelection = 1 };
            var meal = new MealItem { Id = Guid.NewGuid(), VersionGroupId = Guid.NewGuid(), Sku = $"{code}-W{week}-{weekday}", Category = category, Status = "ACTIVE", IsAvailable = true,
                Translations = [new MealItemTranslation { Id = Guid.NewGuid(), LanguageCode = "en", Name = $"{code} Week {week} {weekday}" }] };
            slot.Options.Add(new MealPlanSlotOption { Id = Guid.NewGuid(), MealItem = meal, IsAvailable = true, DisplayOrder = 1 });
            day.Slots.Add(slot); plan.Days.Add(day);
        }
        db.Add(plan); await db.SaveChangesAsync(); return plan;
    }

    [UpcomingPostgresFact]
    public async Task Menu_browsing_maps_all_four_weeks_and_repeats_from_each_saved_template_start()
    {
        var setup = await ApprovalClient(); await using var factory = setup.Factory; using var client = setup.Client;
        var plan = await SeedBrowsingCycle("PREVIEW", new(2026, 11, 1));
        var dates = new[] { ("2026-11-01", 1, "Sunday"), ("2026-11-02", 1, "Monday"), ("2026-11-06", 1, "Friday"), ("2026-11-07", 1, "Saturday"),
            ("2026-11-08", 2, "Sunday"), ("2026-11-15", 3, "Sunday"), ("2026-11-22", 4, "Sunday"), ("2026-11-29", 1, "Sunday"), ("2027-01-03", 2, "Sunday") };
        foreach (var (date, week, weekday) in dates)
        {
            var response = await client.GetFromJsonAsync<JsonElement>($"/api/v1/guest/meal-plans/PREVIEW/menu?date={date}&language=en");
            var data = response.GetProperty("data");
            Assert.Equal(week, data.GetProperty("rotationWeek").GetInt32());
            Assert.Equal($"PREVIEW Week {week} {weekday}", data.GetProperty("slots")[0].GetProperty("meals")[0].GetProperty("name").GetString());
            var publicMenu = await client.GetFromJsonAsync<JsonElement>($"/api/v1/meal-plans/{plan.Id}/meals?date={date}&language=en");
            Assert.Contains($"PREVIEW Week {week} {weekday}", publicMenu.GetRawText());
        }
        Assert.Single(await db.MealPlanTemplateDays.FromSqlRaw("SELECT * FROM public.meal_plan_template_days WHERE day_of_week='SUNDAY' AND week_number=1").Where(d => d.MealPlanTemplateId == plan.Id).ToListAsync());
        await SeedBrowsingCycle("OTHER_PREVIEW", new(2026, 11, 8));
        var other = await client.GetFromJsonAsync<JsonElement>("/api/v1/guest/meal-plans/OTHER_PREVIEW/menu?date=2026-11-15&language=en");
        Assert.Equal(2, other.GetProperty("data").GetProperty("rotationWeek").GetInt32());
        Assert.Contains("OTHER_PREVIEW Week 2 Sunday", other.GetRawText());
    }

    [UpcomingPostgresFact]
    public async Task Menu_browsing_keeps_unconfigured_dates_selectable_and_returns_empty_without_fallback()
    {
        var setup = await ApprovalClient(); await using var factory = setup.Factory; using var client = setup.Client;
        var plan = await SeedBrowsingCycle("EMPTY_PREVIEW", new(2026, 11, 1));
        var initialHome = await client.GetFromJsonAsync<JsonElement>("/api/v1/guest/home?planCode=EMPTY_PREVIEW&language=en");
        var initialDate = initialHome.GetProperty("data").GetProperty("weeklyCalendar").EnumerateArray().Single(d => d.GetProperty("isSelected").GetBoolean());
        Assert.Equal("2026-10-28", initialDate.GetProperty("date").GetString()); // Existing browsing lead time; no jump to another configured week/day.
        foreach (var date in new[] { "2026-11-18", "2026-10-31" })
        {
            var response = await client.GetAsync($"/api/v1/guest/meal-plans/EMPTY_PREVIEW/menu?date={date}&language=en");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Empty((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("slots").EnumerateArray());
            var home = await client.GetFromJsonAsync<JsonElement>($"/api/v1/guest/home?date={date}&planCode=EMPTY_PREVIEW&language=en");
            Assert.All(home.GetProperty("data").GetProperty("weeklyCalendar").EnumerateArray(), d => Assert.True(d.GetProperty("isAvailable").GetBoolean()));
            var publicMeals = await client.GetFromJsonAsync<JsonElement>($"/api/v1/meal-plans/{plan.Id}/meals?date={date}");
            Assert.Empty(publicMeals.GetProperty("data").EnumerateArray());
        }
        var calendar = await client.GetFromJsonAsync<JsonElement>($"/api/v1/meal-plans/{plan.Id}/calendar?startDate=2026-11-15&numberOfDays=7");
        Assert.Equal(7, calendar.GetProperty("data").GetArrayLength());
        Assert.All(calendar.GetProperty("data").EnumerateArray(), d => Assert.True(d.GetProperty("isAvailable").GetBoolean()));
        var missing = calendar.GetProperty("data").EnumerateArray().Single(d => d.GetProperty("date").GetString() == "2026-11-18");
        Assert.False(missing.TryGetProperty("templateDayId", out var id) && id.ValueKind != JsonValueKind.Null);
        plan.ValidUntil = new(2026, 11, 20);
        await db.SaveChangesAsync();
        var expired = await client.GetFromJsonAsync<JsonElement>("/api/v1/guest/meal-plans/EMPTY_PREVIEW/menu?date=2026-11-22&language=en");
        Assert.Empty(expired.GetProperty("data").GetProperty("slots").EnumerateArray());
        var expiredCalendar = await client.GetFromJsonAsync<JsonElement>($"/api/v1/meal-plans/{plan.Id}/calendar?startDate=2026-11-22&numberOfDays=1");
        Assert.True(expiredCalendar.GetProperty("data")[0].GetProperty("isAvailable").GetBoolean());
        Assert.False(expiredCalendar.GetProperty("data")[0].TryGetProperty("templateDayId", out var expiredId) && expiredId.ValueKind != JsonValueKind.Null);
    }

    [UpcomingPostgresFact]
    public async Task Menu_browsing_sorts_by_meal_type_order_before_slot_order_in_both_languages()
    {
        var setup = await ApprovalClient(); await using var factory = setup.Factory; using var client = setup.Client;
        var plan = await SeedBrowsingCycle("SORT_PREVIEW", new(2026, 11, 1));
        var day = plan.Days.Single(d => d.WeekNumber == 1 && d.MenuWeekday == MenuWeekday.Sunday);
        day.Slots.Single().DisplayOrder = 100;
        foreach (var (code, name, order) in new[] { ("SORT_LUNCH", "Lunch", 20), ("SORT_DINNER", "Dinner", 30), ("SORT_SNACK", "Snack", 30) })
        {
            var type = new MealType { Id = Guid.NewGuid(), Code = code, DisplayOrder = order, IsActive = true,
                Translations = [new MealTypeTranslation { Id = Guid.NewGuid(), LanguageCode = "en", Name = name }, new MealTypeTranslation { Id = Guid.NewGuid(), LanguageCode = "ar", Name = "عربي " + name }] };
            var slot = new MealPlanTemplateSlot { Id = Guid.NewGuid(), MealType = type, DisplayOrder = 40-order, IsActive = true, MaximumSelection = 1 };
            slot.Options.Add(new MealPlanSlotOption { Id = Guid.NewGuid(), MealItemId = day.Slots.First().Options.First().MealItemId, IsAvailable = true });
            day.Slots.Add(slot);
            db.Add(slot);
        }
        await db.SaveChangesAsync();
        var expected = new[] { "SORT_PREVIEW_TYPE", "SORT_LUNCH", "SORT_DINNER", "SORT_SNACK" };
        foreach (var language in new[] { "en", "ar" })
        {
            var menu = await client.GetFromJsonAsync<JsonElement>($"/api/v1/guest/meal-plans/SORT_PREVIEW/menu?date=2026-11-01&language={language}");
            Assert.Equal(expected, menu.GetProperty("data").GetProperty("slots").EnumerateArray().Select(s => s.GetProperty("mealTime").GetProperty("code").GetString()));
            var home = await client.GetFromJsonAsync<JsonElement>($"/api/v1/guest/home?date=2026-11-01&planCode=SORT_PREVIEW&language={language}");
            var metadata = home.GetProperty("data").GetProperty("mealPlans").EnumerateArray().Single(p => p.GetProperty("code").GetString() == "SORT_PREVIEW");
            Assert.Equal(expected, metadata.GetProperty("slots").EnumerateArray().Select(s => s.GetProperty("mealTime").GetProperty("code").GetString()));
            var lookup = await client.GetFromJsonAsync<JsonElement>($"/api/v1/meal-types?language={language}");
            Assert.Equal(expected, lookup.GetProperty("data").EnumerateArray().Select(t => t.GetProperty("code").GetString()).Where(c => c?.StartsWith("SORT_") == true));
        }
        var lunchType = await db.MealTypes.SingleAsync(t => t.Code == "SORT_LUNCH");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/v1/admin/meal-types/{lunchType.Id}",
            new UpsertMealTypeRequest("SORT_LUNCH", "Lunch", 5, true, "الغداء"))).StatusCode);
        var reordered = await client.GetFromJsonAsync<JsonElement>("/api/v1/guest/meal-plans/SORT_PREVIEW/menu?date=2026-11-01&language=en");
        Assert.Equal("SORT_LUNCH", reordered.GetProperty("data").GetProperty("slots")[0].GetProperty("mealTime").GetProperty("code").GetString());
    }
}
