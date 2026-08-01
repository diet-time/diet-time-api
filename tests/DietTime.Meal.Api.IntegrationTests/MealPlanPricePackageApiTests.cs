using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DietTime.Domain;
using DietTime.Meal.Api.Controllers;
using DietTime.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed class MealPlanPricePackageApiTests : IAsyncLifetime
{
    private readonly bool enabled = Environment.GetEnvironmentVariable("RUN_INTEGRATION_TESTS") == "true";
    private PostgreSqlContainer? postgres;
    private ApiFactory? factory;
    private HttpClient? client;
    private Guid planId;
    private Guid weekId;
    private Guid inactiveId;

    public async Task InitializeAsync()
    {
        if (!enabled) return;
        postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("diettime_price_package_test")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
        await postgres.StartAsync();
        factory = new ApiFactory(postgres.GetConnectionString());
        client = factory.CreateClient();
        await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        client?.Dispose();
        if (factory is not null) await factory.DisposeAsync();
        if (postgres is not null) await postgres.DisposeAsync();
    }

    [Fact]
    public void Package_endpoints_retain_admin_authorization_metadata()
    {
        var authorize = Assert.Single(typeof(AdminController).GetCustomAttributes(typeof(AuthorizeAttribute), true));
        Assert.Contains("Admin", ((AuthorizeAttribute)authorize).Roles);
    }

    [Fact]
    public async Task Package_list_supports_pagination_default_order_search_and_active_filter()
    {
        if (!enabled) return;
        using var ordered = JsonDocument.Parse(await client!.GetStringAsync(
            "/api/v1/admin/meal-plan-price-packages?page=1&pageSize=2"));
        var data = ordered.RootElement.GetProperty("data");
        Assert.Equal(2, data.GetArrayLength());
        Assert.Equal("DAY", data[0].GetProperty("code").GetString());
        Assert.Equal(3, ordered.RootElement.GetProperty("meta").GetProperty("totalCount").GetInt32());

        foreach (var search in new[] { "week", "One Week", "أسبوع" })
        {
            using var found = JsonDocument.Parse(await client.GetStringAsync(
                $"/api/v1/admin/meal-plan-price-packages?search={Uri.EscapeDataString(search)}"));
            Assert.Equal("WEEK", found.RootElement.GetProperty("data")[0].GetProperty("code").GetString());
        }

        using var active = JsonDocument.Parse(await client.GetStringAsync(
            "/api/v1/admin/meal-plan-price-packages?isActive=false"));
        Assert.All(active.RootElement.GetProperty("data").EnumerateArray(),
            item => Assert.False(item.GetProperty("isActive").GetBoolean()));
    }

    [Fact]
    public async Task Package_create_validates_and_rejects_duplicate_normalized_code()
    {
        if (!enabled) return;
        var created = await client!.PostAsJsonAsync("/api/v1/admin/meal-plan-price-packages", new
        {
            code = "corporate month",
            nameEn = "Corporate Month",
            nameAr = "شهر الشركات",
            durationDays = 30,
            displayOrder = 4,
            isActive = true
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Contains("CORPORATE_MONTH", await created.Content.ReadAsStringAsync());

        var duplicate = await client!.PostAsJsonAsync("/api/v1/admin/meal-plan-price-packages", new
        {
            code = " Corporate   Month ",
            nameEn = "Duplicate",
            nameAr = "مكرر",
            durationDays = 30,
            displayOrder = 5,
            isActive = true
        });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Contains("duplicate_package_code", await duplicate.Content.ReadAsStringAsync());

        var invalid = await client!.PostAsJsonAsync("/api/v1/admin/meal-plan-price-packages", new
        {
            code = "ZERO",
            nameEn = "Zero",
            nameAr = "صفر",
            durationDays = 0,
            displayOrder = 0,
            isActive = true
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Referenced_package_allows_label_update_but_blocks_duration_change()
    {
        if (!enabled) return;
        await CreatePackagePriceAsync(weekId, null, 300m, DateTimeOffset.UtcNow.AddDays(-1));

        var labels = await client!.PutAsJsonAsync($"/api/v1/admin/meal-plan-price-packages/{weekId}", new
        {
            code = "WEEK",
            nameEn = "Updated Week",
            nameAr = "أسبوع محدث",
            durationDays = 6,
            displayOrder = 7,
            isActive = true
        });
        Assert.Equal(HttpStatusCode.NoContent, labels.StatusCode);

        var duration = await client!.PutAsJsonAsync($"/api/v1/admin/meal-plan-price-packages/{weekId}", new
        {
            code = "WEEK",
            nameEn = "Updated Week",
            nameAr = "أسبوع محدث",
            durationDays = 7,
            displayOrder = 7,
            isActive = true
        });
        Assert.Equal(HttpStatusCode.Conflict, duration.StatusCode);
        Assert.Contains("package_duration_in_use", await duration.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Deactivation_removes_lookup_item_but_preserves_historical_pricing()
    {
        if (!enabled) return;
        var created = await CreatePackagePriceAsync(weekId, null, 300m, DateTimeOffset.UtcNow.AddDays(-1));
        var priceId = await ReadCreatedIdAsync(created);
        var status = await client!.PatchAsJsonAsync(
            $"/api/v1/admin/meal-plan-price-packages/{weekId}/status", new { isActive = false });
        Assert.Equal(HttpStatusCode.NoContent, status.StatusCode);

        var lookup = await client!.GetStringAsync("/api/v1/admin/meal-plan-price-packages/lookup");
        Assert.DoesNotContain(weekId.ToString(), lookup, StringComparison.OrdinalIgnoreCase);
        var historical = await client.GetAsync($"/api/v1/admin/meal-plan-pricing/{priceId}");
        Assert.Equal(HttpStatusCode.OK, historical.StatusCode);
        Assert.Contains("WEEK", await historical.Content.ReadAsStringAsync());

        var rejected = await CreatePackagePriceAsync(weekId, null, 350m, DateTimeOffset.UtcNow.AddMonths(1));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("package_inactive", await rejected.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Package_pricing_resolves_duration_returns_package_and_preserves_overlap_and_legacy_paths()
    {
        if (!enabled) return;
        var effectiveFrom = DateTimeOffset.UtcNow.AddDays(2);
        var created = await CreatePackagePriceAsync(weekId, null, 300m, effectiveFrom);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var list = JsonDocument.Parse(await client!.GetStringAsync(
            $"/api/v1/admin/meal-plan-pricing?packageId={weekId}"));
        var price = Assert.Single(list.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal(6, price.GetProperty("durationDays").GetInt32());
        Assert.Equal(weekId, price.GetProperty("mealPlanPricePackageId").GetGuid());
        Assert.Equal("WEEK", price.GetProperty("packageCode").GetString());

        var mismatch = await CreatePackagePriceAsync(weekId, 7, 400m, effectiveFrom.AddMonths(2));
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.Contains("package_duration_mismatch", await mismatch.Content.ReadAsStringAsync());

        var overlap = await CreatePackagePriceAsync(weekId, null, 325m, effectiveFrom.AddHours(1));
        Assert.Equal(HttpStatusCode.Conflict, overlap.StatusCode);

        var legacy = await client.PostAsJsonAsync("/api/v1/admin/meal-plan-pricing", new
        {
            mealPlanTemplateId = planId,
            durationDays = 10,
            mealsPerDay = 3,
            snacksPerDay = 1,
            currencyCode = "QAR",
            amount = 450m,
            effectiveFrom = effectiveFrom,
            effectiveUntil = (DateTimeOffset?)null,
            isActive = true
        });
        Assert.Equal(HttpStatusCode.Created, legacy.StatusCode);
    }

    private Task<HttpResponseMessage> CreatePackagePriceAsync(
        Guid packageId, int? durationDays, decimal amount, DateTimeOffset effectiveFrom) =>
        client!.PostAsJsonAsync("/api/v1/admin/meal-plan-pricing", new
        {
            mealPlanTemplateId = planId,
            mealPlanPricePackageId = packageId,
            durationDays,
            mealsPerDay = 3,
            snacksPerDay = 1,
            currencyCode = "QAR",
            amount,
            effectiveFrom,
            effectiveUntil = (DateTimeOffset?)null,
            isActive = true
        });

    private static async Task<Guid> ReadCreatedIdAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("data").GetProperty("id").GetGuid();
    }

    private async Task SeedAsync()
    {
        using var scope = factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DietTimeDbContext>();
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        planId = Guid.NewGuid();
        weekId = Guid.NewGuid();
        inactiveId = Guid.NewGuid();
        db.MealPlanTemplates.Add(new()
        {
            Id = planId,
            VersionGroupId = planId,
            Code = "PACKAGE_TEST",
            PlanType = "STANDARD",
            DurationDays = 6,
            IsLatest = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
            RowVersion = 1,
            Translations = [new() { LanguageCode = "en", Name = "Package Test", CreatedAt = now, UpdatedAt = now }]
        });
        db.MealPlanPricePackages.AddRange(
            new() { Id = Guid.NewGuid(), Code = "DAY", NameEn = "One Day", NameAr = "يوم واحد", DurationDays = 1, DisplayOrder = 1, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = weekId, Code = "WEEK", NameEn = "One Week", NameAr = "أسبوع واحد", DurationDays = 6, DisplayOrder = 2, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = inactiveId, Code = "MONTH", NameEn = "One Month", NameAr = "شهر واحد", DurationDays = 24, DisplayOrder = 3, IsActive = false, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
    }
}
