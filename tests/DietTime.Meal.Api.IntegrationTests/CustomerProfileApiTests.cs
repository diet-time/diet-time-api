using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed class CustomerProfileApiTests : IAsyncLifetime
{
    private readonly bool enabled =
        Environment.GetEnvironmentVariable("RUN_INTEGRATION_TESTS") == "true";
    private PostgreSqlContainer? postgres;
    private ApiFactory? factory;
    private HttpClient? client;
    private Guid userId;
    private Guid secondUserId;
    private Guid allergenId;

    public async Task InitializeAsync()
    {
        if (!enabled)
            return;

        postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("diettime_profile_test")
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
        if (factory is not null)
            await factory.DisposeAsync();
        if (postgres is not null)
            await postgres.DisposeAsync();
    }

    [Fact]
    public async Task Creates_partial_profile_then_completes_it_with_bmi_and_nutrition()
    {
        if (!enabled) return;
        Authenticate(userId);

        var partial = await client!.PutAsJsonAsync("/api/v1/customer/profile", new
        {
            genderCode = "MALE",
            preferredLanguage = "en",
            onboardingStatus = "IN_PROGRESS",
            preferences = Array.Empty<object>(),
            allergens = Array.Empty<object>()
        });

        Assert.Equal(HttpStatusCode.OK, partial.StatusCode);
        using (var json = JsonDocument.Parse(await partial.Content.ReadAsStringAsync()))
        {
            var data = json.RootElement.GetProperty("data");
            Assert.Equal(userId, data.GetProperty("userId").GetGuid());
            Assert.Equal("IN_PROGRESS", data.GetProperty("onboardingStatus").GetString());
            Assert.False(data.TryGetProperty("nutritionTarget", out _));
        }

        var completed = await client!.PutAsJsonAsync("/api/v1/customer/profile", CompleteRequest());

        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        using var completedJson = JsonDocument.Parse(await completed.Content.ReadAsStringAsync());
        var completedData = completedJson.RootElement.GetProperty("data");
        Assert.Equal(26.78m, completedData.GetProperty("bmi").GetDecimal());
        Assert.Equal("OVERWEIGHT", completedData.GetProperty("bmiCategoryCode").GetString());
        Assert.Equal("COMPLETED", completedData.GetProperty("onboardingStatus").GetString());
        Assert.True(completedData.TryGetProperty("onboardingCompletedAt", out _));
        Assert.Equal("MIFFLIN_ST_JEOR", completedData.GetProperty("nutritionTarget").GetProperty("calculationMethod").GetString());
        Assert.Single(completedData.GetProperty("preferences").EnumerateArray());
        Assert.Single(completedData.GetProperty("allergens").EnumerateArray());

        var read = await client!.GetAsync("/api/v1/customer/profile");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Contains("\"userId\"", await read.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Updates_preferred_name_without_replacing_profile_answers()
    {
        if (!enabled) return;
        Authenticate(userId);
        await client!.PutAsJsonAsync("/api/v1/customer/profile", CompleteRequest());

        var response = await client!.PatchAsJsonAsync(
            "/api/v1/customer/profile/preferred-name",
            new { preferredName = "Noor" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("Noor", data.GetProperty("preferredName").GetString());
        Assert.Equal("LOSE_WEIGHT", data.GetProperty("goalCode").GetString());

        using var scope = factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DietTimeDbContext>();
        var profile = await db.CustomerProfiles.SingleAsync(x => x.UserId == userId);
        Assert.Equal("Noor", profile.PreferredName);
        Assert.Equal("LOSE_WEIGHT", profile.GoalCode);
    }

    [Fact]
    public async Task Updates_existing_children_in_place_and_empty_lists_remove_them()
    {
        if (!enabled) return;
        Authenticate(userId);
        using var initial = JsonDocument.Parse(
            await (await client!.PutAsJsonAsync("/api/v1/customer/profile", CompleteRequest()))
                .Content.ReadAsStringAsync());
        var originalPreferenceId = initial.RootElement.GetProperty("data").GetProperty("preferences")[0].GetProperty("id").GetGuid();
        var originalAllergenLinkId = initial.RootElement.GetProperty("data").GetProperty("allergens")[0].GetProperty("id").GetGuid();

        var update = CompleteRequest(
            preferences: [new { preferenceCode = "HIGH_PROTEIN", preferenceType = "DIET_STYLE", preferencePriority = 3 }],
            allergens: [new { allergenId, severityCode = "MILD", medicallyConfirmed = false, notes = "Updated" }]);
        using var updated = JsonDocument.Parse(
            await (await client!.PutAsJsonAsync("/api/v1/customer/profile", update))
                .Content.ReadAsStringAsync());
        Assert.Equal(originalPreferenceId, updated.RootElement.GetProperty("data").GetProperty("preferences")[0].GetProperty("id").GetGuid());
        Assert.Equal(originalAllergenLinkId, updated.RootElement.GetProperty("data").GetProperty("allergens")[0].GetProperty("id").GetGuid());
        Assert.Equal(3, updated.RootElement.GetProperty("data").GetProperty("preferences")[0].GetProperty("preferencePriority").GetInt32());

        var remove = CompleteRequest(preferences: [], allergens: []);
        using var removed = JsonDocument.Parse(
            await (await client!.PutAsJsonAsync("/api/v1/customer/profile", remove))
                .Content.ReadAsStringAsync());
        Assert.Empty(removed.RootElement.GetProperty("data").GetProperty("preferences").EnumerateArray());
        Assert.Empty(removed.RootElement.GetProperty("data").GetProperty("allergens").EnumerateArray());
    }

    [Fact]
    public async Task Rejects_invalid_allergens_without_creating_a_profile()
    {
        if (!enabled) return;
        Authenticate(userId);
        var invalidId = Guid.NewGuid();

        var response = await client!.PutAsJsonAsync("/api/v1/customer/profile", new
        {
            preferredLanguage = "en",
            onboardingStatus = "IN_PROGRESS",
            preferences = Array.Empty<object>(),
            allergens = new[] { new { allergenId = invalidId, medicallyConfirmed = false } }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(invalidId.ToString(), await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NotFound, (await client!.GetAsync("/api/v1/customer/profile")).StatusCode);
    }

    [Fact]
    public async Task Profiles_are_isolated_by_the_authenticated_user()
    {
        if (!enabled) return;
        Authenticate(userId);
        await client!.PutAsJsonAsync("/api/v1/customer/profile", new
        {
            genderCode = "MALE",
            preferredLanguage = "en",
            onboardingStatus = "IN_PROGRESS"
        });

        Authenticate(secondUserId);
        Assert.Equal(HttpStatusCode.NotFound, (await client!.GetAsync("/api/v1/customer/profile")).StatusCode);
        await client.PutAsJsonAsync("/api/v1/customer/profile", new
        {
            genderCode = "FEMALE",
            preferredLanguage = "ar",
            onboardingStatus = "IN_PROGRESS"
        });

        Authenticate(userId);
        using var first = JsonDocument.Parse(await client.GetStringAsync("/api/v1/customer/profile"));
        Assert.Equal(userId, first.RootElement.GetProperty("data").GetProperty("userId").GetGuid());
        Assert.Equal("MALE", first.RootElement.GetProperty("data").GetProperty("genderCode").GetString());
    }

    [Fact]
    public async Task Missing_user_identity_is_unauthorized()
    {
        if (!enabled) return;

        Assert.Equal(HttpStatusCode.Unauthorized, (await client!.GetAsync("/api/v1/customer/profile")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.PutAsJsonAsync("/api/v1/customer/profile", new
            {
                preferredLanguage = "en",
                onboardingStatus = "IN_PROGRESS"
            })).StatusCode);
    }

    [Fact]
    public async Task Row_version_rejects_a_stale_concurrent_update()
    {
        if (!enabled) return;
        Authenticate(userId);
        await client!.PutAsJsonAsync("/api/v1/customer/profile", new
        {
            preferredLanguage = "en",
            onboardingStatus = "IN_PROGRESS"
        });

        using var firstScope = factory!.Services.CreateScope();
        using var secondScope = factory.Services.CreateScope();
        var firstDb = firstScope.ServiceProvider.GetRequiredService<DietTimeDbContext>();
        var secondDb = secondScope.ServiceProvider.GetRequiredService<DietTimeDbContext>();
        var first = await firstDb.CustomerProfiles.SingleAsync(x => x.UserId == userId);
        var stale = await secondDb.CustomerProfiles.SingleAsync(x => x.UserId == userId);
        first.GoalCode = "FIRST";
        first.RowVersion++;
        await firstDb.SaveChangesAsync();
        stale.GoalCode = "STALE";
        stale.RowVersion++;

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondDb.SaveChangesAsync());
    }

    [Fact]
    public async Task Database_failure_rolls_back_the_entire_profile_transaction()
    {
        if (!enabled) return;
        using (var setupScope = factory!.Services.CreateScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<DietTimeDbContext>();
            await setupDb.Database.ExecuteSqlRawAsync(
                "ALTER TABLE public.customer_profile_preferences ADD CONSTRAINT ck_profile_test_rollback CHECK (preference_code <> 'ROLLBACK')");
        }
        Authenticate(userId);

        var response = await client!.PutAsJsonAsync("/api/v1/customer/profile", new
        {
            preferredLanguage = "en",
            onboardingStatus = "IN_PROGRESS",
            preferences = new[]
            {
                new { preferenceCode = "ROLLBACK", preferenceType = "TEST", preferencePriority = 1 }
            },
            allergens = Array.Empty<object>()
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<DietTimeDbContext>();
        Assert.False(await verificationDb.CustomerProfiles.AnyAsync(x => x.UserId == userId));
    }

    private object CompleteRequest(object[]? preferences = null, object[]? allergens = null) => new
    {
        genderCode = "MALE",
        dateOfBirth = "1990-06-15",
        heightCm = 175,
        weightKg = 82,
        goalCode = "LOSE_WEIGHT",
        dailyRoutineCode = "OFFICE_WORK",
        activityLevelCode = "LIGHT_ACTIVITY",
        preferredLanguage = "en",
        onboardingStatus = "COMPLETED",
        preferences = preferences ??
        [
            new { preferenceCode = "HIGH_PROTEIN", preferenceType = "DIET_STYLE", preferencePriority = 5 }
        ],
        allergens = allergens ??
        [
            new { allergenId, severityCode = "SEVERE", medicallyConfirmed = true, notes = "Avoid cross-contamination." }
        ]
    };

    private void Authenticate(Guid id)
    {
        client!.DefaultRequestHeaders.Remove("X-Development-User-Id");
        client.DefaultRequestHeaders.Add("X-Development-User-Id", id.ToString());
    }

    private async Task SeedAsync()
    {
        using var scope = factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DietTimeDbContext>();
        await db.Database.EnsureCreatedAsync();
        userId = Guid.NewGuid();
        secondUserId = Guid.NewGuid();
        allergenId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.Users.AddRange(
            new ApplicationUser { Id = userId, UserName = "profile-one@example.test", NormalizedUserName = "PROFILE-ONE@EXAMPLE.TEST", Email = "profile-one@example.test", NormalizedEmail = "PROFILE-ONE@EXAMPLE.TEST" },
            new ApplicationUser { Id = secondUserId, UserName = "profile-two@example.test", NormalizedUserName = "PROFILE-TWO@EXAMPLE.TEST", Email = "profile-two@example.test", NormalizedEmail = "PROFILE-TWO@EXAMPLE.TEST" });
        db.Allergens.Add(new Allergen
        {
            Id = allergenId,
            Code = "PEANUTS",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
            Translations =
            [
                new() { LanguageCode = "en", Name = "Peanuts", CreatedAt = now, UpdatedAt = now },
                new() { LanguageCode = "ar", Name = "Peanuts Arabic", CreatedAt = now, UpdatedAt = now }
            ]
        });
        await db.SaveChangesAsync();
    }
}
