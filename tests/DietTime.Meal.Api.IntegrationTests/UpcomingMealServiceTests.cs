using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed class UpcomingPostgresFactAttribute : FactAttribute
{
    public UpcomingPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("UPCOMING_MEALS_TEST_CONNECTION")))
            Skip = "Set UPCOMING_MEALS_TEST_CONNECTION to an isolated PostgreSQL instance.";
    }
}

public sealed class UpcomingMealServiceTests : IAsyncLifetime
{
    private readonly string? adminConnection = Environment.GetEnvironmentVariable("UPCOMING_MEALS_TEST_CONNECTION");
    private readonly string database = "diettime_upcoming_test_" + Guid.NewGuid().ToString("N");
    private string connection = "";
    private DietTimeDbContext db = null!;
    private readonly TestClock clock = new();
    private Order order = null!;
    private Guid userId;

    public async Task InitializeAsync()
    {
        if (adminConnection is null) return;
        await using (var admin = new NpgsqlConnection(adminConnection))
        {
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE {database}", admin);
            await command.ExecuteNonQueryAsync();
        }
        connection = new NpgsqlConnectionStringBuilder(adminConnection) { Database = database }.ConnectionString;
        db = Context();
        await db.Database.EnsureCreatedAsync();
        await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        if (adminConnection is null) return;
        await db.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", admin);
        await command.ExecuteNonQueryAsync();
    }

    private DietTimeDbContext Context() => new(new DbContextOptionsBuilder<DietTimeDbContext>()
        .UseNpgsql(connection).UseSnakeCaseNamingConvention().Options);
    private UpcomingMealService Service(DietTimeDbContext? context = null) => new(context ?? db, clock,
        Options.Create(new MealSelectionOptions { CutoffTime = new(19, 0), DaysBeforeDelivery = 1 }), new TestStorage());

    [UpcomingPostgresFact]
    public async Task Purchase_creates_assignments_and_customer_HTTP_endpoints_save_and_protect_them()
    {
        var address = new CustomerAddress { Id = Guid.NewGuid(), CustomerProfileId = order.CustomerProfileId,
            AddressType = "HOME", Area = "Doha", IsActive = true };
        var delivery = new DeliveryTimeSlot { Id = Guid.NewGuid(), Code = "MORNING", Name = "Morning", NameAr = "Morning",
            StartTime = new(9, 0), EndTime = new(11, 0), IsActive = true };
        db.AddRange(address, delivery);
        await db.SaveChangesAsync();
        await using var factory = new ApiFactory(connection, services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.PostConfigure<DietTime.Infrastructure.TwilioWhatsAppOptions>(o => o.Enabled = false);
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Development-User-Id", userId.ToString());
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders") {
            Content = JsonContent.Create(new PlaceOrderRequest(order.CustomerProfileId,
                order.MealPlanTemplateId, order.MealPlanPriceId, address.Id, delivery.Id, order.StartDate,
                [1, 2, 3, 4, 6, 7], order.Meals.Select(m => new PlaceOrderMealRequest(m.MealTypeId, m.Quantity)).ToArray()))
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var placed = await client.SendAsync(request);
        var body = await placed.Content.ReadAsStringAsync();
        Assert.True(placed.StatusCode == HttpStatusCode.Created, body);
        var newOrderId = JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
        Assert.Equal(48, await db.Set<CustomerMealSelection>().CountAsync(s => s.ServiceDay.OrderId == newOrderId));
        var days = await client.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{newOrderId}/upcoming-meals");
        var dayId = days.GetProperty("data")[0].GetProperty("subscriptionServiceDayId").GetGuid();
        var menuPath = $"/api/v1/orders/{newOrderId}/service-days/{dayId}/menu";
        var rawMenu = await client.GetFromJsonAsync<JsonElement>(menuPath);
        var menu = rawMenu.GetProperty("data").Deserialize<DailyMealMenuResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var replacement = Alternative(menu);
        var changePath = $"/api/v1/orders/{newOrderId}/service-days/{dayId}/meal-selections/{menu.SelectedMeals[0].SelectionId}";
        var changed = await client.PutAsJsonAsync(changePath,
            new ReplaceAssignedMealRequest(replacement.MealItemId, replacement.SlotOptionId, menu.Revision));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var reopened = await client.GetFromJsonAsync<JsonElement>(menuPath);
        Assert.Equal("customized", reopened.GetProperty("data").GetProperty("status").GetString());
        client.DefaultRequestHeaders.Remove("X-Development-User-Id");
        client.DefaultRequestHeaders.Add("X-Development-User-Id", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(menuPath)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Development-User-Id");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(menuPath)).StatusCode);
    }

    [UpcomingPostgresFact]
    public async Task Assigns_exact_quantities_once_for_every_day_and_reopens_saved_choices()
    {
        var service = Service();
        await service.InitializeAsync(order, default);
        await service.InitializeAsync(order, default);
        Assert.Equal(12, await db.Set<SubscriptionServiceDay>().CountAsync());
        Assert.Equal(48, await db.Set<CustomerMealSelection>().CountAsync());
        var days = await service.GetDaysAsync(order.Id, userId, default);
        Assert.All(days!, d => Assert.NotEqual(DayOfWeek.Friday, d.DeliveryDate.DayOfWeek));
        Assert.Equal(1, days![5].MenuWeekNumber);
        Assert.Equal(2, days[6].MenuWeekNumber);
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 16, 0, 0, TimeSpan.Zero), days[0].CutoffAt);
        var menu = (await service.GetMenuAsync(order.Id, days[0].SubscriptionServiceDayId, userId, "en", default))!;
        Assert.Equal(3, menu.SelectedMeals.Count(m => m.Meal.Category == "meal"));
        Assert.Single(menu.SelectedMeals.Where(m => m.Meal.Category == "snack"));
        Assert.Equal(4, menu.SelectedMeals.Select(m => m.Meal.MealItemId).Distinct().Count());
        var replacement = Alternative(menu);
        var changed = await service.ReplaceAsync(order.Id, menu.SubscriptionServiceDayId, menu.SelectedMeals[0].SelectionId,
            userId, "en", new(replacement.MealItemId, replacement.SlotOptionId, menu.Revision), default);
        Assert.Equal("customized", changed.Status);
        Assert.Equal(3, menu.SelectedMeals.Skip(1).Count(old => changed.SelectedMeals.Any(m => m.SelectionId == old.SelectionId && m.Meal.MealItemId == old.Meal.MealItemId)));
        await service.InitializeAsync(order, default);
        await using var fresh = Context();
        var reopened = (await Service(fresh).GetMenuAsync(order.Id, menu.SubscriptionServiceDayId, userId, "en", default))!;
        Assert.Equal(replacement.MealItemId, reopened.SelectedMeals[0].Meal.MealItemId);
        Assert.Equal("customer", reopened.SelectedMeals[0].SelectionSource);
        Assert.Equal(48, await fresh.Set<CustomerMealSelection>().CountAsync());
        Assert.Single(await fresh.Set<CustomerMealSelectionHistory>().ToListAsync());
    }

    [UpcomingPostgresFact]
    public async Task Ownership_cutoff_delivered_and_kitchen_locks_are_enforced()
    {
        await Service().InitializeAsync(order, default);
        var day = await db.Set<SubscriptionServiceDay>().OrderBy(d => d.ServiceDate).FirstAsync();
        Assert.Null(await Service().GetMenuAsync(order.Id, day.Id, Guid.NewGuid(), "en", default));
        var menu = (await Service().GetMenuAsync(order.Id, day.Id, userId, "en", default))!;
        var alt = Alternative(menu);
        clock.Now = menu.CutoffAt!.Value;
        Assert.False((await Service().GetMenuAsync(order.Id, day.Id, userId, "en", default))!.CanEdit);
        var error = await Assert.ThrowsAsync<UpcomingMealException>(() => Service().ReplaceAsync(order.Id, day.Id,
            menu.SelectedMeals[0].SelectionId, userId, "en", new(alt.MealItemId, alt.SlotOptionId, menu.Revision), default));
        Assert.Equal(409, error.StatusCode);
        clock.Now = new(2026, 10, 25, 10, 0, 0, TimeSpan.Zero);
        day.SelectionLocked = true;
        await db.SaveChangesAsync();
        Assert.False((await Service().GetMenuAsync(order.Id, day.Id, userId, "en", default))!.CanEdit);
        day.Status = "DELIVERED";
        await db.SaveChangesAsync();
        Assert.Equal("delivered", (await Service().GetMenuAsync(order.Id, day.Id, userId, "en", default))!.Status);
    }

    [UpcomingPostgresFact]
    public async Task Allergen_restriction_and_inactive_options_are_excluded_and_shortages_flagged()
    {
        var allergen = new Allergen { Id = Guid.NewGuid(), Code = "NUTS", IsActive = true };
        db.Add(allergen);
        db.Add(new CustomerProfileAllergen { Id = Guid.NewGuid(), CustomerProfileId = order.CustomerProfileId, AllergenId = allergen.Id });
        var bad = await db.MealItems.Where(m => m.Sku == "meal-0").SingleAsync();
        bad.Allergens.Add(new MealItemAllergen { MealItemId = bad.Id, AllergenId = allergen.Id });
        var inactive = await db.MealItems.Where(m => m.Sku == "meal-1").SingleAsync();
        inactive.Status = "INACTIVE";
        var restricted = await db.MealItems.Where(m => m.Sku == "meal-2").SingleAsync();
        restricted.IsVegan = false;
        db.Add(new CustomerProfilePreference { Id = Guid.NewGuid(), CustomerProfileId = order.CustomerProfileId, PreferenceCode = "VEGAN", PreferenceType = "DIET_STYLE" });
        await db.SaveChangesAsync();
        await Service().InitializeAsync(order, default);
        var day = await db.Set<SubscriptionServiceDay>().FirstAsync();
        var menu = (await Service().GetMenuAsync(order.Id, day.Id, userId, "en", default))!;
        Assert.Equal("needs_review", menu.Status);
        Assert.False(menu.CanEdit);
        Assert.DoesNotContain(menu.AvailableAlternatives, m => new[] { bad.Id, inactive.Id, restricted.Id }.Contains(m.MealItemId));
        Assert.DoesNotContain(menu.SelectedMeals, m => new[] { bad.Id, inactive.Id, restricted.Id }.Contains(m.Meal.MealItemId));
    }

    [UpcomingPostgresFact]
    public async Task Rejects_other_menu_day_category_and_duplicate_dish_without_mutating_others()
    {
        await Service().InitializeAsync(order, default);
        var day = await db.Set<SubscriptionServiceDay>().OrderBy(d => d.ServiceDate).FirstAsync();
        var menu = (await Service().GetMenuAsync(order.Id, day.Id, userId, "en", default))!;
        var first = menu.SelectedMeals.First(m => m.Meal.Category == "meal");
        var snack = menu.AvailableAlternatives.First(m => m.Category == "snack");
        await Assert.ThrowsAsync<UpcomingMealException>(() => Service().ReplaceAsync(order.Id, day.Id, first.SelectionId,
            userId, "en", new(snack.MealItemId, snack.SlotOptionId, menu.Revision), default));
        var other = menu.SelectedMeals.First(m => m.SelectionId != first.SelectionId && m.Meal.Category == "meal").Meal;
        await Assert.ThrowsAsync<UpcomingMealException>(() => Service().ReplaceAsync(order.Id, day.Id, first.SelectionId,
            userId, "en", new(other.MealItemId, other.SlotOptionId, menu.Revision), default));
        var otherOption = await db.MealPlanSlotOptions.FirstAsync(o => o.Slot.Day.MenuWeekday == MenuWeekday.Wednesday);
        await Assert.ThrowsAsync<UpcomingMealException>(() => Service().ReplaceAsync(order.Id, day.Id, first.SelectionId,
            userId, "en", new(otherOption.MealItemId, otherOption.Id, menu.Revision), default));
        Assert.Empty(await db.Set<CustomerMealSelectionHistory>().ToListAsync());
    }

    [UpcomingPostgresFact]
    public async Task Concurrent_replacements_cannot_overwrite_one_another()
    {
        await Service().InitializeAsync(order, default);
        var day = await db.Set<SubscriptionServiceDay>().OrderBy(d => d.ServiceDate).FirstAsync();
        var menu = (await Service().GetMenuAsync(order.Id, day.Id, userId, "en", default))!;
        var ids = menu.SelectedMeals.Select(m => m.Meal.MealItemId).ToHashSet();
        var options = menu.AvailableAlternatives.Where(m => m.Category == "meal" && !ids.Contains(m.MealItemId)).Take(2).ToArray();
        async Task<int> Attempt(UpcomingMealItemResponse option)
        {
            await using var context = Context();
            try { await Service(context).ReplaceAsync(order.Id, day.Id, menu.SelectedMeals[0].SelectionId,
                userId, "en", new(option.MealItemId, option.SlotOptionId, menu.Revision), default); return 200; }
            catch (UpcomingMealException error) { return error.StatusCode; }
        }
        var results = await Task.WhenAll(options.Select(Attempt));
        Assert.Equal(new[] { 200, 409 }, results.Order().ToArray());
    }

    [UpcomingPostgresFact]
    public async Task Kitchen_summary_uses_saved_customer_dish_and_finalizes_at_cutoff()
    {
        order.Status = "ACTIVE";
        await db.SaveChangesAsync();
        await Service().InitializeAsync(order, default);
        var day = await db.Set<SubscriptionServiceDay>().OrderBy(d => d.ServiceDate).FirstAsync();
        var menu = (await Service().GetMenuAsync(order.Id, day.Id, userId, "en", default))!;
        var replacement = Alternative(menu);
        await Service().ReplaceAsync(order.Id, day.Id, menu.SelectedMeals[0].SelectionId,
            userId, "en", new(replacement.MealItemId, replacement.SlotOptionId, menu.Revision), default);
        clock.Now = menu.CutoffAt!.Value;
        var calendar = new AdminDeliveryCalendarService(db, Options.Create(new MenuRotationOptions()), clock);
        var summary = await calendar.GetPreparationSummaryAsync(day.ServiceDate, default);
        Assert.Equal(4, summary.MealItemCount);
        Assert.Contains(summary.MealTypes.SelectMany(t => t.Items), m => m.MenuItemId == replacement.MealItemId);
        db.ChangeTracker.Clear();
        Assert.True((await db.Set<SubscriptionServiceDay>().SingleAsync(d => d.Id == day.Id)).SelectionLocked);
        Assert.False((await Service().GetMenuAsync(order.Id, day.Id, userId, "en", default))!.CanEdit);
    }

    [UpcomingPostgresFact]
    public async Task Every_rotation_week_and_actual_weekday_resolve_against_the_purchased_template()
    {
        var dates = SubscriptionMenuRules.ServiceDates(order.StartDate, order.StartDate.AddDays(45),
            new HashSet<int> { 1, 2, 3, 4, 6, 7 }).Take(30).ToArray();
        order.EndDate = dates[^1];
        await db.SaveChangesAsync();
        await Service().InitializeAsync(order, default);
        var days = (await Service().GetDaysAsync(order.Id, userId, default))!;
        Assert.Equal(30, days.Count);
        foreach (var sequence in new[] { 1, 6, 7, 12, 13, 18, 19, 24, 25, 30 })
        {
            var day = days[sequence - 1];
            var menu = (await Service().GetMenuAsync(order.Id, day.SubscriptionServiceDayId, userId, "en", default))!;
            var ids = menu.AvailableAlternatives.Select(m => m.SlotOptionId).ToArray();
            var mapped = await db.MealPlanSlotOptions.Where(o => ids.Contains(o.Id))
                .Select(o => new { o.Slot.Day.WeekNumber, o.Slot.Day.MenuWeekday, o.Slot.Day.MealPlanTemplateId }).ToListAsync();
            Assert.All(mapped, option =>
            {
                Assert.Equal(SubscriptionMenuRules.MenuWeek(sequence, 4), option.WeekNumber);
                Assert.Equal(MenuWeekdayExtensions.FromDate(day.DeliveryDate), option.MenuWeekday);
                Assert.Equal(order.MealPlanTemplateId, option.MealPlanTemplateId);
            });
        }
    }

    private static UpcomingMealItemResponse Alternative(DailyMealMenuResponse menu)
    {
        var ids = menu.SelectedMeals.Select(m => m.Meal.MealItemId).ToHashSet();
        return menu.AvailableAlternatives.First(m => m.Category == menu.SelectedMeals[0].Meal.Category && !ids.Contains(m.MealItemId));
    }

    private async Task SeedAsync()
    {
        var now = clock.Now;
        userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "upcoming-test", SecurityStamp = Guid.NewGuid().ToString() });
        var profile = new CustomerProfile { Id = Guid.NewGuid(), UserId = userId, IsActive = true, PreferredLanguage = "en", CreatedAt = now, UpdatedAt = now };
        db.Add(profile);
        var mealType = new MealType { Id = Guid.NewGuid(), Code = "MEAL", IsActive = true };
        var snackType = new MealType { Id = Guid.NewGuid(), Code = "SNACK", IsActive = true };
        db.AddRange(mealType, snackType);
        var category = new MealCategory { Id = Guid.NewGuid(), Code = "BALANCED", IsActive = true };
        db.Add(category);
        var meals = Enumerable.Range(0, 7).Select(i => new MealItem
        {
            Id = Guid.NewGuid(), VersionGroupId = Guid.NewGuid(), Category = category, Sku = $"meal-{i}",
            Status = "ACTIVE", IsAvailable = true, IsVegan = true,
            Translations = [new MealItemTranslation { Id = Guid.NewGuid(), LanguageCode = "en", Name = $"Dish {i}" }]
        }).ToArray();
        db.AddRange(meals);
        var plan = new MealPlanTemplate { Id = Guid.NewGuid(), VersionGroupId = Guid.NewGuid(), Code = "BALANCE", CycleWeeks = 4, IsActive = true, IsPublished = true };
        for (var week = 1; week <= 4; week++)
        foreach (var weekday in Enum.GetValues<MenuWeekday>().Where(d => d != MenuWeekday.Friday))
        {
            var day = new MealPlanTemplateDay { Id = Guid.NewGuid(), WeekNumber = week, MenuWeekday = weekday, IsActive = true };
            foreach (var (type, dishes) in new[] { (mealType, meals.Take(5)), (snackType, meals.Skip(5)) })
            {
                var slot = new MealPlanTemplateSlot { Id = Guid.NewGuid(), MealType = type, IsActive = true, MaximumSelection = 5 };
                var position = 0;
                foreach (var meal in dishes)
                    slot.Options.Add(new MealPlanSlotOption { Id = Guid.NewGuid(), MealItem = meal, IsAvailable = true, IsDefault = position == 0, DisplayOrder = position++ });
                day.Slots.Add(slot);
            }
            plan.Days.Add(day);
        }
        db.Add(plan);
        var package = new MealPackageOption { Id = Guid.NewGuid(), Name = "3 Meals + 1 Snack", MealCount = 3, SnackCount = 1, IsActive = true };
        db.Add(package);
        db.Add(new MealPlanPricePackage { Code = "FORTNIGHT", NameEn = "2 Weeks", NameAr = "2 Weeks", DurationDays = 12, IsActive = true });
        var price = new MealPlanPrice { Id = Guid.NewGuid(), Plan = plan, PackageOption = package, PackageCode = "FORTNIGHT",
            MealsPerDay = 3, SnacksPerDay = 1, DurationDays = 12, Amount = 1299, IsActive = true };
        db.Add(price);
        var dates = SubscriptionMenuRules.ServiceDates(new(2026, 10, 27), new(2026, 12, 1), new HashSet<int> { 1, 2, 3, 4, 6, 7 }).Take(12).ToArray();
        order = new Order { Id = Guid.NewGuid(), OrderNumber = "ORD-TEST", IdempotencyKey = Guid.NewGuid().ToString(),
            CustomerProfileId = profile.Id, MealPlanTemplateId = plan.Id, MealPlanPriceId = price.Id,
            StartDate = dates[0], EndDate = dates[^1], PlanName = "Complete Balance", DeliveryStartTime = new(9, 0), CreatedAt = now, UpdatedAt = now };
        foreach (var weekday in new[] { 1, 2, 3, 4, 6, 7 })
            order.DeliveryDays.Add(new OrderDeliveryDay { Id = Guid.NewGuid(), DayOfWeek = weekday });
        order.Meals.Add(new OrderMeal { Id = Guid.NewGuid(), MealTypeId = mealType.Id, Quantity = 3 });
        order.Meals.Add(new OrderMeal { Id = Guid.NewGuid(), MealTypeId = snackType.Id, Quantity = 1 });
        db.Add(order);
        await db.SaveChangesAsync();
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 25, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class TestStorage : IStorageUrlService
    {
        public long MaxUploadSizeBytes => 1024;
        public string GetPublicUrl(string key) => "https://example.test/" + key;
        public string GetThumbnailUrl(string key) => GetPublicUrl(key);
        public Task UploadAsync(string k, Stream s, string t, CancellationToken ct) => Task.CompletedTask;
        public Task<StoredObject?> DownloadAsync(string k, CancellationToken ct) => Task.FromResult<StoredObject?>(null);
        public Task DeleteAsync(string k, CancellationToken ct) => Task.CompletedTask;
    }
}

public sealed class SubscriptionMenuRulesTests
{
    [Theory]
    [InlineData(1, 1)] [InlineData(6, 1)] [InlineData(7, 2)] [InlineData(12, 2)]
    [InlineData(13, 3)] [InlineData(18, 3)] [InlineData(19, 4)] [InlineData(24, 4)] [InlineData(25, 1)]
    public void Four_week_cycle_uses_service_sequence(int sequence, int expected) =>
        Assert.Equal(expected, SubscriptionMenuRules.MenuWeek(sequence, 4));
}
