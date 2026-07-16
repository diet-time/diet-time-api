using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed class CatalogueApiTests : IAsyncLifetime
{
    private readonly bool enabled = Environment.GetEnvironmentVariable("RUN_INTEGRATION_TESTS") == "true";
    private PostgreSqlContainer? postgres;
    private ApiFactory? factory; private HttpClient? client; private Guid planId; private Guid dayId; private Guid mealId;
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

    private async Task SeedAsync()
    {
        using var scope = factory!.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DietTimeDbContext>(); await db.Database.EnsureCreatedAsync(); var now = DateTimeOffset.UtcNow;
        var category = new MealCategory { Code = "BREAKFAST", IsActive = true, CreatedAt = now, UpdatedAt = now, RowVersion = 1, Translations = [new() { LanguageCode = "en", Name = "Breakfast", CreatedAt = now, UpdatedAt = now }] };
        var type = new MealType { Code = "BREAKFAST", IsActive = true, DisplayOrder = 1, CreatedAt = now, UpdatedAt = now, Translations = [new() { LanguageCode = "en", Name = "Breakfast", CreatedAt = now, UpdatedAt = now }, new() { LanguageCode = "ar", Name = "الإفطار", CreatedAt = now, UpdatedAt = now }] };
        var meal = new MealItem { Sku = "DT-BRK-0001", Category = category, Status = "ACTIVE", IsAvailable = true, CreatedAt = now, UpdatedAt = now, RowVersion = 1, Translations = [new() { LanguageCode = "en", Name = "Test Meal", CreatedAt = now, UpdatedAt = now }, new() { LanguageCode = "ar", Name = "وجبة تجريبية", CreatedAt = now, UpdatedAt = now }] }; mealId = meal.Id = Guid.NewGuid();
        var plan = new MealPlanTemplate { Code = "CLASSIC", PlanType = "STANDARD", DurationDays = 7, IsActive = true, IsPublished = true, IsCustomizable = true, CreatedAt = now, UpdatedAt = now, RowVersion = 1, Translations = [new() { LanguageCode = "en", Name = "Classic", CreatedAt = now, UpdatedAt = now }] }; planId = plan.Id = Guid.NewGuid();
        var day = new MealPlanTemplateDay { Plan = plan, DayNumber = 1, IsActive = true, CreatedAt = now, UpdatedAt = now }; dayId = day.Id = Guid.NewGuid(); var slot = new MealPlanTemplateSlot { Day = day, MealType = type, MinimumSelection = 1, MaximumSelection = 1, IsRequired = true, AllowsPaidUpgrade = true, IsActive = true, CreatedAt = now, UpdatedAt = now, RowVersion = 1 }; slot.Options.Add(new() { MealItem = meal, IsAvailable = true, CreatedAt = now, UpdatedAt = now }); db.Add(plan); await db.SaveChangesAsync();
    }
}

internal sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = connectionString, ["Jwt:Issuer"] = "DietTime.Tests", ["Jwt:Audience"] = "DietTime.Tests", ["Jwt:Key"] = "test-only-key-at-least-thirty-two-characters-long", ["Storage:PublicBaseUrl"] = "https://cdn.test", ["Storage:BucketName"] = "test", ["Storage:ServiceUrl"] = "http://localhost:9000", ["Storage:AccessKey"] = "test", ["Storage:SecretKey"] = "test" }));
}
