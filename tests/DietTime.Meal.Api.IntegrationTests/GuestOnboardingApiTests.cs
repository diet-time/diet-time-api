using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DietTime.Application;
using DietTime.Domain;
using DietTime.Infrastructure;
using DietTime.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed class GuestOnboardingApiTests : IAsyncLifetime
{
    private readonly bool enabled =
        Environment.GetEnvironmentVariable("RUN_INTEGRATION_TESTS") == "true";
    private PostgreSqlContainer? postgres;
    private ApiFactory? factory;
    private HttpClient? client;
    private Guid activeAllergenId;
    private Guid inactiveAllergenId;
    private Guid authenticatedUserId;

    public async Task InitializeAsync()
    {
        if (!enabled) return;
        postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("diettime_guest_test")
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
    public async Task Session_and_progressive_profile_flow_never_persists_the_raw_token()
    {
        if (!enabled) return;
        var token = await NewSessionAsync();

        var partial = await PutProfileAsync(token, new
        {
            genderCode = "MALE",
            dateOfBirth = "1990-06-15",
            preferredLanguage = "en",
            onboardingStatus = "IN_PROGRESS",
            preferences = Array.Empty<object>(),
            allergens = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.OK, partial.StatusCode);
        using (var partialJson = JsonDocument.Parse(await partial.Content.ReadAsStringAsync()))
        {
            var partialData = partialJson.RootElement.GetProperty("data");
            Assert.Equal("BODY_MEASUREMENTS", partialData.GetProperty("nextStepCode").GetString());
            Assert.Equal(14, partialData.GetProperty("completionPercentage").GetInt32());
            Assert.True(partialData.GetProperty("shouldShowOnboarding").GetBoolean());
        }

        using (var scope = factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DietTimeDbContext>();
            var stored = await db.CustomerProfiles.SingleAsync();
            Assert.Null(stored.UserId);
            Assert.NotEqual(token, stored.GuestTokenHash);
            Assert.Equal(
                scope.ServiceProvider.GetRequiredService<IGuestTokenHasher>().Hash(token),
                stored.GuestTokenHash);
        }

        var completed = await PutProfileAsync(token, CompleteRequest());
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        using var completedJson = JsonDocument.Parse(await completed.Content.ReadAsStringAsync());
        var data = completedJson.RootElement.GetProperty("data");
        Assert.Equal(26.78m, data.GetProperty("bmi").GetDecimal());
        Assert.Equal("PROFILE_COMPLETED", data.GetProperty("onboardingStatus").GetString());
        Assert.Equal("PROFILE_COMPLETED", data.GetProperty("nextStepCode").GetString());
        Assert.Equal(100, data.GetProperty("completionPercentage").GetInt32());
        Assert.False(data.GetProperty("shouldShowOnboarding").GetBoolean());
        Assert.Equal("MIFFLIN_ST_JEOR", data.GetProperty("nutritionTarget").GetProperty("calculationMethod").GetString());

        var read = await GetAsync("/api/v1/guest/profile", token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.DoesNotContain("guestTokenHash", await read.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Optional_empty_steps_are_distinct_from_unvisited_and_partial_saves_preserve_fields()
    {
        if (!enabled) return;
        var token = await NewSessionAsync();

        using var required = JsonDocument.Parse(
            await (await PutProfileAsync(token, new
            {
                genderCode = "FEMALE",
                dateOfBirth = "1992-03-20",
                heightCm = 165,
                weightKg = 62,
                goalCode = "MAINTAIN_WEIGHT",
                dailyRoutineCode = "OFFICE_WORK",
                activityLevelCode = "LIGHT_ACTIVITY",
                preferredLanguage = "en",
                onboardingStatus = "PROFILE_COMPLETED"
            })).Content.ReadAsStringAsync());
        var requiredData = required.RootElement.GetProperty("data");
        Assert.Equal("ALLERGENS", requiredData.GetProperty("nextStepCode").GetString());
        Assert.Equal("IN_PROGRESS", requiredData.GetProperty("onboardingStatus").GetString());

        using var allergens = JsonDocument.Parse(
            await (await PutProfileAsync(token, new
            {
                allergensConfirmed = true,
                allergens = Array.Empty<object>()
            })).Content.ReadAsStringAsync());
        var allergenData = allergens.RootElement.GetProperty("data");
        Assert.Equal("PREFERENCES", allergenData.GetProperty("nextStepCode").GetString());
        Assert.Equal(86, allergenData.GetProperty("completionPercentage").GetInt32());
        Assert.Equal("FEMALE", allergenData.GetProperty("genderCode").GetString());
        Assert.Equal(165m, allergenData.GetProperty("heightCm").GetDecimal());
        Assert.Empty(allergenData.GetProperty("allergens").EnumerateArray());

        using var preferences = JsonDocument.Parse(
            await (await PutProfileAsync(token, new
            {
                preferencesConfirmed = true,
                preferences = Array.Empty<object>()
            })).Content.ReadAsStringAsync());
        var preferenceData = preferences.RootElement.GetProperty("data");
        Assert.Equal("PROFILE_COMPLETED", preferenceData.GetProperty("nextStepCode").GetString());
        Assert.Equal(100, preferenceData.GetProperty("completionPercentage").GetInt32());
        Assert.False(preferenceData.GetProperty("shouldShowOnboarding").GetBoolean());
        Assert.Empty(preferenceData.GetProperty("preferences").EnumerateArray());
    }

    [Fact]
    public async Task Profile_updates_preserve_children_and_empty_lists_remove_them()
    {
        if (!enabled) return;
        var token = await NewSessionAsync();
        using var initial = JsonDocument.Parse(
            await (await PutProfileAsync(token, CompleteRequest())).Content.ReadAsStringAsync());
        var preferenceId = initial.RootElement.GetProperty("data").GetProperty("preferences")[0].GetProperty("id").GetGuid();
        var allergenLinkId = initial.RootElement.GetProperty("data").GetProperty("allergens")[0].GetProperty("id").GetGuid();

        using var updated = JsonDocument.Parse(
            await (await PutProfileAsync(token, CompleteRequest(
                [new { preferenceCode = "HIGH_PROTEIN", preferenceType = "DIET_STYLE", preferencePriority = 3 }],
                [new { allergenId = activeAllergenId, severityCode = "MILD", medicallyConfirmed = false, notes = "Changed" }])))
                .Content.ReadAsStringAsync());
        Assert.Equal(preferenceId, updated.RootElement.GetProperty("data").GetProperty("preferences")[0].GetProperty("id").GetGuid());
        Assert.Equal(allergenLinkId, updated.RootElement.GetProperty("data").GetProperty("allergens")[0].GetProperty("id").GetGuid());

        using var removed = JsonDocument.Parse(
            await (await PutProfileAsync(token, CompleteRequest([], []))).Content.ReadAsStringAsync());
        Assert.Empty(removed.RootElement.GetProperty("data").GetProperty("preferences").EnumerateArray());
        Assert.Empty(removed.RootElement.GetProperty("data").GetProperty("allergens").EnumerateArray());
    }

    [Fact]
    public async Task Invalid_inactive_expired_and_isolated_guest_sessions_are_enforced()
    {
        if (!enabled) return;
        var token = await NewSessionAsync();
        var invalidAllergens = await PutProfileAsync(token, new
        {
            preferredLanguage = "en",
            onboardingStatus = "IN_PROGRESS",
            allergens = new[]
            {
                new { allergenId = inactiveAllergenId, medicallyConfirmed = false }
            }
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidAllergens.StatusCode);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await GetAsync("/api/v1/guest/profile", "malformed")).StatusCode);

        var otherToken = await NewSessionAsync();
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await GetAsync("/api/v1/guest/profile", otherToken)).StatusCode);

        await PutProfileAsync(token, new
        {
            preferredLanguage = "en",
            onboardingStatus = "IN_PROGRESS"
        });
        using (var scope = factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DietTimeDbContext>();
            var profile = await db.CustomerProfiles.SingleAsync();
            profile.GuestTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await GetAsync("/api/v1/guest/profile", token)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_first_saves_create_only_one_profile()
    {
        if (!enabled) return;
        var token = await NewSessionAsync();
        var request = new
        {
            genderCode = "MALE",
            preferredLanguage = "en",
            onboardingStatus = "IN_PROGRESS"
        };

        var responses = await Task.WhenAll(
            PutProfileAsync(token, request),
            PutProfileAsync(token, request),
            PutProfileAsync(token, request),
            PutProfileAsync(token, request));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        using var scope = factory!.Services.CreateScope();
        Assert.Equal(
            1,
            await scope.ServiceProvider.GetRequiredService<DietTimeDbContext>()
                .CustomerProfiles.CountAsync());
    }

    [Fact]
    public async Task Recommendations_exclude_confirmed_allergen_conflicts()
    {
        if (!enabled) return;
        var token = await NewSessionAsync();
        await PutProfileAsync(token, CompleteRequest());

        var response = await GetAsync("/api/v1/guest/plan-recommendations", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var recommendations = json.RootElement.GetProperty("data").EnumerateArray().ToArray();
        Assert.Contains(recommendations, x => x.GetProperty("planCode").GetString() == "SAFE");
        Assert.DoesNotContain(recommendations, x => x.GetProperty("planCode").GetString() == "CONFLICT");
    }

    [Fact]
    public async Task Cleanup_deletes_only_expired_guest_profiles_past_retention()
    {
        if (!enabled) return;
        var token = await NewSessionAsync();
        await PutProfileAsync(token, new
        {
            preferredLanguage = "en",
            onboardingStatus = "IN_PROGRESS"
        });
        using var scope = factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DietTimeDbContext>();
        var guest = await db.CustomerProfiles.SingleAsync();
        guest.GuestTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(-8);
        db.CustomerProfiles.Add(new CustomerProfile
        {
            Id = Guid.NewGuid(),
            UserId = authenticatedUserId,
            PreferredLanguage = "en",
            OnboardingStatus = "PROFILE_COMPLETED",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-100),
            UpdatedAt = DateTimeOffset.UtcNow,
            RowVersion = 1
        });
        await db.SaveChangesAsync();

        var deleted = await scope.ServiceProvider
            .GetRequiredService<IGuestProfileCleanupService>()
            .DeleteExpiredBatchAsync(default);

        Assert.Equal(1, deleted);
        Assert.False(await db.CustomerProfiles.AnyAsync(x => x.Id == guest.Id));
        Assert.True(await db.CustomerProfiles.AnyAsync(x => x.UserId == authenticatedUserId));
    }

    [Fact]
    public async Task Guest_session_creation_is_rate_limited()
    {
        if (!enabled) return;
        var responses = new List<HttpResponseMessage>();
        for (var index = 0; index < 11; index++)
            responses.Add(await client!.PostAsync("/api/v1/guest/session", null));

        Assert.Equal(HttpStatusCode.TooManyRequests, responses[^1].StatusCode);
    }

    private async Task<string> NewSessionAsync()
    {
        var response = await client!.PostAsync("/api/v1/guest/session", null);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("data").GetProperty("guestToken").GetString()!;
    }

    private async Task<HttpResponseMessage> PutProfileAsync(string token, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/guest/profile")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("X-Guest-Token", token);
        return await client!.SendAsync(request);
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Guest-Token", token);
        return await client!.SendAsync(request);
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
        onboardingStatus = "PROFILE_COMPLETED",
        allergensConfirmed = true,
        preferencesConfirmed = true,
        preferences = preferences ??
        [
            new { preferenceCode = "HIGH_PROTEIN", preferenceType = "DIET_STYLE", preferencePriority = 5 }
        ],
        allergens = allergens ??
        [
            new { allergenId = activeAllergenId, severityCode = "SEVERE", medicallyConfirmed = true, notes = (string?)null }
        ]
    };

    private async Task SeedAsync()
    {
        using var scope = factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DietTimeDbContext>();
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        activeAllergenId = Guid.NewGuid();
        inactiveAllergenId = Guid.NewGuid();
        authenticatedUserId = Guid.NewGuid();
        var allergen = new Allergen
        {
            Id = activeAllergenId,
            Code = "PEANUTS",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
            Translations = [new() { LanguageCode = "en", Name = "Peanuts", CreatedAt = now, UpdatedAt = now }]
        };
        db.Allergens.Add(allergen);
        db.Allergens.Add(new Allergen
        {
            Id = inactiveAllergenId,
            Code = "INACTIVE",
            IsActive = false,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.Users.Add(new ApplicationUser
        {
            Id = authenticatedUserId,
            UserName = "cleanup@example.test",
            NormalizedUserName = "CLEANUP@EXAMPLE.TEST"
        });
        AddPlan(db, "SAFE", allergen: null, now);
        AddPlan(db, "CONFLICT", allergen, now);
        await db.SaveChangesAsync();
    }

    private static void AddPlan(
        DietTimeDbContext db,
        string code,
        Allergen? allergen,
        DateTimeOffset now)
    {
        var category = new MealCategory
        {
            Code = $"{code}_CATEGORY",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
            RowVersion = 1
        };
        var mealType = new MealType
        {
            Code = $"{code}_TYPE",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        var meal = new MealItem
        {
            Id = Guid.NewGuid(),
            Sku = $"{code}_MEAL",
            Category = category,
            Status = "ACTIVE",
            IsAvailable = true,
            CreatedAt = now,
            UpdatedAt = now,
            RowVersion = 1
        };
        meal.VersionGroupId = meal.Id;
        if (allergen is not null)
        {
            meal.Allergens.Add(new MealItemAllergen
            {
                Allergen = allergen,
                AllergenId = allergen.Id,
                CreatedAt = now
            });
        }
        var plan = new MealPlanTemplate
        {
            Id = Guid.NewGuid(),
            Code = code,
            PlanType = "STANDARD",
            DurationDays = 7,
            IsCustomizable = true,
            IsActive = true,
            IsPublished = true,
            CreatedAt = now,
            UpdatedAt = now,
            RowVersion = 1,
            Translations =
            [
                new() { LanguageCode = "en", Name = code, ShortDescription = $"{code} plan", CreatedAt = now, UpdatedAt = now }
            ]
        };
        plan.VersionGroupId = plan.Id;
        var day = new MealPlanTemplateDay
        {
            Plan = plan,
            MenuWeekday = MenuWeekday.Thursday,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        var slot = new MealPlanTemplateSlot
        {
            Day = day,
            MealType = mealType,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
            RowVersion = 1
        };
        slot.Options.Add(new MealPlanSlotOption
        {
            MealItem = meal,
            IsAvailable = true,
            CreatedAt = now,
            UpdatedAt = now
        });
        day.Slots.Add(slot);
        plan.Days.Add(day);
        db.Add(plan);
    }
}
