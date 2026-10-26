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
        await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX ux_customer_meal_selection_legacy_type ON public.customer_meal_selections(subscription_service_day_id, meal_type_id) WHERE category IS NULL");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE public.customer_meal_selections ADD CONSTRAINT test_category CHECK (category IN ('meal','snack'))");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE public.subscription_service_days ADD CONSTRAINT test_selection_status CHECK (selection_status IN ('PENDING','PARTIAL','COMPLETED','DEFAULTED','LOCKED'))");
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
        Options.Create(new MealSelectionOptions { CutoffTime = new(21, 0), DaysBeforeDelivery = 1 }), new TestStorage());


    private async Task<TomorrowMenuResponse> PrepareAsync()
    {
        clock.Now = new(2026, 10, 26, 10, 0, 0, TimeSpan.Zero);
        await Service().InitializeAsync(order, default);
        return (await Service().GetTomorrowAsync(order.Id, userId, "en", default))!;
    }
    private static ConfirmMealsRequest Request(TomorrowMenuResponse menu) => new(
        menu.SelectedMeals.Where(m => m.Category == "meal").Select(m => m.MealItemId).ToArray(),
        menu.SelectedMeals.Where(m => m.Category == "snack").Select(m => m.MealItemId).ToArray());

    [UpcomingPostgresFact]
    public async Task Tomorrow_defaults_are_provisional_and_confirmation_is_permanently_locked()
    {
        var menu = await PrepareAsync();
        Assert.Equal(new DateOnly(2026, 10, 27), menu.DeliveryDate);
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 18, 0, 0, TimeSpan.Zero), menu.CutoffAt);
        Assert.Equal("DEFAULT_PENDING", menu.SelectionStatus);
        Assert.True(menu.CanConfirm); Assert.False(menu.SelectionLocked);
        Assert.Equal(3, menu.MealCount); Assert.Equal(1, menu.SnackCount);
        var counts = await db.Set<CustomerMealSelection>().CountAsync();
        var request = Request(menu);
        // Reorder existing dishes: tests a dish-index swap without losing positions/history.
        request = request with { MealMenuItemIds = request.MealMenuItemIds.Reverse().ToArray() };
        var saved = await Service().ConfirmAsync(order.Id, menu.SubscriptionServiceDayId!.Value, userId, "en", request, default);
        Assert.Equal("CUSTOMER_CONFIRMED", saved.SelectionStatus);
        Assert.True(saved.SelectionLocked); Assert.False(saved.CanConfirm);
        Assert.Empty(saved.AvailableMeals); Assert.Empty(saved.AvailableSnacks);
        Assert.Equal(counts, await db.Set<CustomerMealSelection>().CountAsync());
        await using var fresh = Context();
        var reopened = (await Service(fresh).GetTomorrowAsync(order.Id, userId, "en", default))!;
        Assert.Equal(saved.SelectedMenuItemIds.Order(), reopened.SelectedMenuItemIds.Order());
        var error = await Assert.ThrowsAsync<UpcomingMealException>(() => Service(fresh).ConfirmAsync(order.Id,
            menu.SubscriptionServiceDayId.Value, userId, "en", request, default));
        Assert.Equal("already_confirmed", error.Code);
        clock.Now = menu.CutoffAt;
        await Service(fresh).FinalizeDueAsync(default);
        Assert.Equal("CUSTOMER_CONFIRMED", (await Service(fresh).GetTomorrowAsync(order.Id, userId, "en", default))!.SelectionStatus);
    }

    [UpcomingPostgresFact]
    public async Task Confirmation_HTTP_authenticates_checks_ownership_and_returns_saved_dishes()
    {
        var menu = await PrepareAsync();
        await using var factory = new ApiFactory(connection, services => services.AddSingleton<TimeProvider>(clock));
        using var client = factory.CreateClient();
        var getPath = $"/api/v1/orders/{order.Id}/tomorrow-menu";
        var postPath = $"/api/v1/orders/{order.Id}/service-days/{menu.SubscriptionServiceDayId}/confirm-meals";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(getPath)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Development-User-Id", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(getPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(postPath, Request(menu))).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Development-User-Id");
        client.DefaultRequestHeaders.Add("X-Development-User-Id", userId.ToString());
        var response = await client.PostAsJsonAsync(postPath, Request(menu));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.True(data.GetProperty("selectionLocked").GetBoolean());
        Assert.Equal("CUSTOMER_CONFIRMED", data.GetProperty("selectionStatus").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(postPath, Request(menu))).StatusCode);
    }

    [UpcomingPostgresFact]
    public async Task At_exact_cutoff_confirmation_is_rejected_and_finalizer_is_idempotent()
    {
        var menu = await PrepareAsync();
        clock.Now = menu.CutoffAt;
        var error = await Assert.ThrowsAsync<UpcomingMealException>(() => Service().ConfirmAsync(order.Id,
            menu.SubscriptionServiceDayId!.Value, userId, "en", Request(menu), default));
        Assert.Equal("cutoff_passed", error.Code);
        await Service().FinalizeDueAsync(default);
        var rows = await db.Set<CustomerMealSelection>().CountAsync();
        var history = await db.Set<CustomerMealSelectionHistory>().CountAsync();
        await using var fresh = Context();
        await Service(fresh).FinalizeDueAsync(default);
        Assert.Equal(rows, await fresh.Set<CustomerMealSelection>().CountAsync());
        Assert.Equal(history, await fresh.Set<CustomerMealSelectionHistory>().CountAsync());
        Assert.Equal("AUTO_FINALIZED", (await Service(fresh).GetTomorrowAsync(order.Id, userId, "en", default))!.SelectionStatus);
    }

    [UpcomingPostgresFact]
    public async Task Concurrent_confirmations_have_one_winner_and_no_duplicates()
    {
        var menu = await PrepareAsync();
        async Task<bool> Attempt()
        {
            await using var context = Context();
            try { await Service(context).ConfirmAsync(order.Id, menu.SubscriptionServiceDayId!.Value, userId, "en", Request(menu), default); return true; }
            catch (UpcomingMealException e) when (e.Code == "already_confirmed") { return false; }
        }
        var results = await Task.WhenAll(Attempt(), Attempt());
        Assert.Single(results.Where(r => r));
        await using var fresh = Context();
        var rows = await fresh.Set<CustomerMealSelection>().Where(s => s.SubscriptionServiceDayId == menu.SubscriptionServiceDayId && s.Status != "CANCELLED").ToListAsync();
        Assert.Equal(4, rows.Count); Assert.Equal(4, rows.Select(r => r.MenuItemId).Distinct().Count());
    }

    [UpcomingPostgresFact]
    public async Task Concurrent_finalizers_and_confirmation_cannot_overwrite_customer_choice()
    {
        var menu = await PrepareAsync();
        // Hold the row while both operations queue, then advance server time to cutoff.
        await using var blocker = Context();
        await using var tx = await blocker.Database.BeginTransactionAsync();
        var id = menu.SubscriptionServiceDayId!.Value;
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM public.subscription_service_days WHERE id = {id} FOR UPDATE");
        await using var confirmingContext = Context();
        var confirmation = Service(confirmingContext).ConfirmAsync(order.Id, id, userId, "en", Request(menu), default);
        clock.Now = menu.CutoffAt;
        await using var workerContext = Context();
        var finalization = Service(workerContext).FinalizeDueAsync(default);
        await tx.CommitAsync();
        var error = await Assert.ThrowsAsync<UpcomingMealException>(() => confirmation);
        Assert.Equal("cutoff_passed", error.Code);
        await finalization;
        await using var retry1 = Context(); await using var retry2 = Context();
        await Task.WhenAll(Service(retry1).FinalizeDueAsync(default), Service(retry2).FinalizeDueAsync(default));
        await using var fresh = Context();
        var day = await fresh.Set<SubscriptionServiceDay>().SingleAsync(d => d.Id == id);
        Assert.True(day.SelectionLocked); Assert.NotNull(day.AutoFinalizedAt); Assert.Null(day.CustomerConfirmedAt);
    }

    [UpcomingPostgresFact]
    public async Task Invalid_counts_duplicates_categories_and_unavailable_dishes_leave_defaults_unchanged()
    {
        var menu = await PrepareAsync();
        var good = Request(menu); var id = menu.SubscriptionServiceDayId!.Value;
        foreach (var bad in new[] {
            good with { MealMenuItemIds = [] },
            good with { MealMenuItemIds = Enumerable.Repeat(good.MealMenuItemIds[0], 3).ToArray() },
            good with { SnackMenuItemIds = [good.MealMenuItemIds[0]] },
            good with { SnackMenuItemIds = [Guid.NewGuid()] } })
        {
            await using var context = Context();
            await Assert.ThrowsAsync<UpcomingMealException>(() => Service(context).ConfirmAsync(order.Id, id, userId, "en", bad, default));
        }
        await using var fresh = Context();
        var after = (await Service(fresh).GetTomorrowAsync(order.Id, userId, "en", default))!;
        Assert.Equal(menu.SelectedMenuItemIds.Order(), after.SelectedMenuItemIds.Order()); Assert.False(after.SelectionLocked);
        Assert.Empty(await fresh.Set<CustomerMealSelectionHistory>().ToListAsync());
    }

    [UpcomingPostgresFact]
    public async Task Cancelled_positions_are_reused_without_violating_unique_indexes()
    {
        var menu = await PrepareAsync();
        var day = await db.Set<SubscriptionServiceDay>().Include(d => d.Selections).SingleAsync(d => d.Id == menu.SubscriptionServiceDayId);
        foreach (var row in day.Selections) row.Status = "CANCELLED";
        await db.SaveChangesAsync();
        var result = await Service().ConfirmAsync(order.Id, day.Id, userId, "en", Request(menu), default);
        Assert.True(result.SelectionLocked);
        Assert.Equal(4, await db.Set<CustomerMealSelection>().CountAsync(s => s.SubscriptionServiceDayId == day.Id));
    }

    [UpcomingPostgresFact]
    public async Task Shortage_flags_review_then_recovers_after_configuration_is_fixed()
    {
        var menu = await PrepareAsync();
        var meals = await db.MealItems.Where(m => m.Sku == "meal-0" || m.Sku == "meal-1" || m.Sku == "meal-2").ToListAsync();
        foreach (var meal in meals) meal.IsAvailable = false;
        await db.SaveChangesAsync(); clock.Now = menu.CutoffAt;
        await Service().FinalizeDueAsync(default);
        var failed = (await Service().GetTomorrowAsync(order.Id, userId, "en", default))!;
        Assert.Equal("NEEDS_REVIEW", failed.SelectionStatus); Assert.False(failed.SelectionLocked);
        var calendar = new AdminDeliveryCalendarService(db, Options.Create(new MenuRotationOptions()), clock);
        var report = await calendar.GetPreparationSummaryAsync(menu.DeliveryDate, default);
        Assert.Empty(report.MealTypes); Assert.NotEmpty(report.OperationalExceptions); Assert.True(report.IsPreliminary);
        foreach (var meal in meals) meal.IsAvailable = true;
        await db.SaveChangesAsync(); await Service().FinalizeDueAsync(default);
        Assert.Equal("AUTO_FINALIZED", (await Service().GetTomorrowAsync(order.Id, userId, "en", default))!.SelectionStatus);
    }

    [UpcomingPostgresFact]
    public async Task Allergens_restrictions_inactive_slots_and_wrong_menu_days_are_excluded()
    {
        var menu = await PrepareAsync();
        var allergen = new Allergen { Id = Guid.NewGuid(), Code = "NUTS", IsActive = true };
        db.Add(allergen); db.Add(new CustomerProfileAllergen { Id = Guid.NewGuid(), CustomerProfileId = order.CustomerProfileId, AllergenId = allergen.Id });
        var bad = await db.MealItems.SingleAsync(m => m.Sku == "meal-0");
        bad.Allergens.Add(new MealItemAllergen { MealItemId = bad.Id, AllergenId = allergen.Id });
        var inactive = await db.MealItems.SingleAsync(m => m.Sku == "meal-1"); inactive.Status = "INACTIVE";
        var restricted = await db.MealItems.SingleAsync(m => m.Sku == "meal-2"); restricted.IsVegan = false;
        db.Add(new CustomerProfilePreference { Id = Guid.NewGuid(), CustomerProfileId = order.CustomerProfileId, PreferenceCode = "VEGAN", PreferenceType = "DIET_STYLE" });
        await db.SaveChangesAsync();
        var refreshed = (await Service().GetTomorrowAsync(order.Id, userId, "en", default))!;
        Assert.DoesNotContain(refreshed.AvailableMeals, m => new[] { bad.Id, inactive.Id, restricted.Id }.Contains(m.MealItemId));
        await Assert.ThrowsAsync<UpcomingMealException>(() => Service().ConfirmAsync(order.Id, menu.SubscriptionServiceDayId!.Value, userId, "en", Request(menu), default));
        var ids = refreshed.AvailableMeals.Concat(refreshed.AvailableSnacks).Select(m => m.SlotOptionId).ToArray();
        var mappings = await db.MealPlanSlotOptions.Where(o => ids.Contains(o.Id)).Select(o => new { o.Slot.Day.MenuWeekday, o.Slot.Day.WeekNumber }).ToListAsync();
        Assert.All(mappings, m => { Assert.Equal(MenuWeekday.Tuesday, m.MenuWeekday); Assert.Equal(1, m.WeekNumber); });
        var slot = await db.MealPlanTemplateSlots.FirstAsync(t => t.Day.WeekNumber == 1 && t.Day.MenuWeekday == MenuWeekday.Tuesday && t.MealType.Code == "MEAL");
        slot.IsActive = false; await db.SaveChangesAsync();
        Assert.Empty((await Service().GetTomorrowAsync(order.Id, userId, "en", default))!.AvailableMeals);
    }

    [UpcomingPostgresFact]
    public async Task Missed_cutoffs_recover_and_kitchen_uses_only_finalized_persisted_dishes()
    {
        var menu = await PrepareAsync();
        var calendar = new AdminDeliveryCalendarService(db, Options.Create(new MenuRotationOptions()), clock);
        var preliminary = await calendar.GetPreparationSummaryAsync(menu.DeliveryDate, default);
        Assert.True(preliminary.IsPreliminary); Assert.Empty(preliminary.MealTypes);
        clock.Now = menu.CutoffAt.AddDays(2); // server was unavailable for two days
        await Service().FinalizeDueAsync(default);
        var report = await calendar.GetPreparationSummaryAsync(menu.DeliveryDate, default);
        Assert.False(report.IsPreliminary); Assert.Equal(4, report.MealItemCount);
        Assert.Equal(menu.SelectedMenuItemIds.Order(), report.MealTypes.SelectMany(t => t.Items).Select(i => i.MenuItemId).Order());
        Assert.Equal("AUTO_FINALIZED", Assert.Single(report.Selections).FinalizationStatus);
        Assert.DoesNotContain(report.MealTypes, t => t.MealTypeName.StartsWith("Meal "));
    }

    [UpcomingPostgresFact]
    public async Task Thursday_has_no_delivery_and_Friday_opens_Saturday_only()
    {
        await PrepareAsync(); clock.Now = new(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal("NO_DELIVERY", (await Service().GetTomorrowAsync(order.Id, userId, "en", default))!.SelectionStatus);
        clock.Now = clock.Now.AddDays(1);
        var saturday = (await Service().GetTomorrowAsync(order.Id, userId, "en", default))!;
        Assert.Equal(DayOfWeek.Saturday, saturday.DeliveryDate.DayOfWeek); Assert.True(saturday.CanConfirm);
        var other = await db.Set<SubscriptionServiceDay>().FirstAsync(d => d.ServiceDate > saturday.DeliveryDate);
        var future = (await Service().GetMenuAsync(order.Id, other.Id, userId, "en", default))!;
        Assert.Empty(future.AvailableAlternatives); Assert.False(future.CanEdit);
        await Assert.ThrowsAsync<UpcomingMealException>(() => Service().ConfirmAsync(order.Id, other.Id, userId, "en", Request(saturday), default));
    }

    [UpcomingPostgresFact]
    public async Task Four_week_mapping_and_saved_template_are_used_for_each_service_sequence()
    {
        var dates = SubscriptionMenuRules.ServiceDates(order.StartDate, order.StartDate.AddDays(45), new HashSet<int> { 1,2,3,4,6,7 }).Take(30).ToArray();
        order.EndDate = dates[^1]; await db.SaveChangesAsync(); await Service().InitializeAsync(order, default);
        var days = await db.Set<SubscriptionServiceDay>().OrderBy(d => d.ServiceSequence).ToListAsync();
        foreach (var day in days)
        {
            clock.Now = SubscriptionMenuRules.QatarCutoff(day.ServiceDate, 1, new(12, 0));
            var menu = (await Service().GetTomorrowAsync(order.Id, userId, "en", default))!;
            var ids = menu.AvailableMeals.Concat(menu.AvailableSnacks).Select(m => m.SlotOptionId).ToArray();
            Assert.NotEmpty(ids);
            var mapped = await db.MealPlanSlotOptions.Where(o => ids.Contains(o.Id)).Select(o => new { o.Slot.Day.WeekNumber, o.Slot.Day.MenuWeekday, o.Slot.Day.MealPlanTemplateId }).ToListAsync();
            Assert.All(mapped, m => { Assert.Equal(SubscriptionMenuRules.MenuWeek(day.ServiceSequence!.Value,4),m.WeekNumber); Assert.Equal(MenuWeekdayExtensions.FromDate(day.ServiceDate),m.MenuWeekday); Assert.Equal(order.MealPlanTemplateId,m.MealPlanTemplateId); });
        }
    }


    [UpcomingPostgresFact]
    public async Task Failure_during_replacement_rolls_back_cancelled_rows_and_confirmation()
    {
        var menu = await PrepareAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION test_fail_confirmation() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.selection_source = 'CUSTOMER' AND NEW.selection_number = 2 AND NEW.status = 'SELECTED' THEN RAISE EXCEPTION 'injected write failure'; END IF; RETURN NEW; END $$");
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER test_fail_confirmation BEFORE UPDATE ON public.customer_meal_selections FOR EACH ROW EXECUTE FUNCTION test_fail_confirmation()");
        await using var context = Context();
        await Assert.ThrowsAsync<DbUpdateException>(() => Service(context).ConfirmAsync(order.Id, menu.SubscriptionServiceDayId!.Value, userId, "en", Request(menu), default));
        await using var fresh = Context();
        var after = (await Service(fresh).GetTomorrowAsync(order.Id, userId, "en", default))!;
        Assert.False(after.SelectionLocked); Assert.Equal("DEFAULT_PENDING", after.SelectionStatus);
        Assert.Equal(menu.SelectedMenuItemIds.Order(), after.SelectedMenuItemIds.Order());
        Assert.Empty(await fresh.Set<CustomerMealSelectionHistory>().ToListAsync());
        Assert.All(await fresh.Set<CustomerMealSelection>().Where(r => r.SubscriptionServiceDayId == menu.SubscriptionServiceDayId).ToListAsync(), r => Assert.Equal("SELECTED", r.Status));
    }

    [UpcomingPostgresFact]
    public async Task Inactive_subscription_and_incomplete_defaults_do_not_bypass_server_rules()
    {
        var menu = await PrepareAsync();
        order.Status = "CANCELLED"; await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<UpcomingMealException>(() => Service().ConfirmAsync(order.Id, menu.SubscriptionServiceDayId!.Value, userId, "en", Request(menu), default));
        Assert.Equal("subscription_inactive", error.Code);
        Assert.False((await Service().GetTomorrowAsync(order.Id,userId,"en",default))!.CanConfirm);
        order.Status = "ACTIVE"; await db.SaveChangesAsync();
        var day = await db.Set<SubscriptionServiceDay>().Include(d => d.Selections).SingleAsync(d => d.Id == menu.SubscriptionServiceDayId);
        day.Selections.First().Status = "CANCELLED";
        day.ReviewReason = "Incomplete defaults"; await db.SaveChangesAsync();
        var refreshed = (await Service().GetTomorrowAsync(order.Id,userId,"en",default))!;
        Assert.True(refreshed.CanConfirm); Assert.NotEmpty(refreshed.AvailableMeals);
        var confirmed = await Service().ConfirmAsync(order.Id, day.Id,userId,"en",Request(menu),default);
        Assert.Equal("CUSTOMER_CONFIRMED",confirmed.SelectionStatus); Assert.Null(confirmed.ReviewReason);
    }


    [UpcomingPostgresFact]
    public async Task One_failed_delivery_is_flagged_without_blocking_other_due_deliveries()
    {
        var menu = await PrepareAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION test_fail_first_default() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.subscription_service_day_id = (SELECT id FROM public.subscription_service_days WHERE service_date = DATE '2026-10-27') AND NEW.status = 'SELECTED' THEN RAISE EXCEPTION 'injected default write failure'; END IF; RETURN NEW; END $$");
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER test_fail_first_default BEFORE UPDATE ON public.customer_meal_selections FOR EACH ROW EXECUTE FUNCTION test_fail_first_default()");
        clock.Now = menu.CutoffAt.AddDays(1);
        await Service().FinalizeDueAsync(default);
        await using var fresh = Context();
        var days = await fresh.Set<SubscriptionServiceDay>().OrderBy(d => d.ServiceDate).Take(2).ToListAsync();
        Assert.False(days[0].SelectionLocked); Assert.NotNull(days[0].ReviewReason);
        Assert.True(days[1].SelectionLocked); Assert.NotNull(days[1].AutoFinalizedAt);
        Assert.All(await fresh.Set<CustomerMealSelection>().Where(r => r.SubscriptionServiceDayId == menu.SubscriptionServiceDayId).ToListAsync(), r => Assert.Equal("SELECTED",r.Status));
        await fresh.Database.ExecuteSqlRawAsync("DROP TRIGGER test_fail_first_default ON public.customer_meal_selections");
        await Service(fresh).FinalizeDueAsync(default);
        Assert.NotNull((await fresh.Set<SubscriptionServiceDay>().SingleAsync(d => d.Id == menu.SubscriptionServiceDayId)).AutoFinalizedAt);
    }

    [UpcomingPostgresFact]
    public async Task Legacy_locked_status_cannot_be_confirmed_or_silently_auto_finalized()
    {
        var menu = await PrepareAsync();
        var day = await db.Set<SubscriptionServiceDay>().SingleAsync(d => d.Id == menu.SubscriptionServiceDayId);
        day.SelectionStatus = "LOCKED"; // Legacy status is authoritative even without the flag.
        await db.SaveChangesAsync();
        var readOnly = (await Service().GetTomorrowAsync(order.Id, userId, "en", default))!;
        Assert.True(readOnly.SelectionLocked); Assert.False(readOnly.CanConfirm); Assert.Empty(readOnly.AvailableMeals);
        var error = await Assert.ThrowsAsync<UpcomingMealException>(() => Service().ConfirmAsync(order.Id, day.Id, userId, "en", Request(menu), default));
        Assert.Equal("already_confirmed", error.Code);
        clock.Now = menu.CutoffAt;
        await Service().FinalizeDueAsync(default);
        Assert.Null(day.AutoFinalizedAt); Assert.Null(day.CustomerConfirmedAt);
        var report = await new AdminDeliveryCalendarService(db, Options.Create(new MenuRotationOptions()), clock)
            .GetPreparationSummaryAsync(menu.DeliveryDate, default);
        Assert.NotEmpty(report.OperationalExceptions); Assert.Empty(report.MealTypes);
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
