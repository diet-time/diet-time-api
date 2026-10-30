using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Questionnaire_is_owned_by_authenticated_user_and_partial_updates_preserve_answers()
    {
        var allergen = new Allergen { Id = Guid.NewGuid(), Code = "TEST_NUTS", IsActive = true };
        db.AddRange(allergen,
            new CustomerProfilePreference { Id = Guid.NewGuid(), CustomerProfileId = order.CustomerProfileId, PreferenceCode = "HIGH_PROTEIN", PreferencePriority = 5 },
            new CustomerProfileAllergen { Id = Guid.NewGuid(), CustomerProfileId = order.CustomerProfileId, AllergenId = allergen.Id });
        await db.SaveChangesAsync();
        await using var factory = new ApiFactory(connection);
        using var client = factory.CreateClient();
        var body = new { customerProfileId = Guid.NewGuid(), genderCode = "FEMALE", onboardingStatus = "IN_PROGRESS" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync("/api/v1/customer/profile", body)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Development-User-Id", userId.ToString());
        var partial = await client.PutAsJsonAsync("/api/v1/customer/profile", body);
        Assert.Equal(HttpStatusCode.OK, partial.StatusCode);
        var saved = (await partial.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(order.CustomerProfileId, saved.GetProperty("id").GetGuid());
        Assert.False(saved.GetProperty("isQuestionnaireComplete").GetBoolean());
        Assert.Equal(70, saved.GetProperty("weightKg").GetDecimal()); // Omitted answers survive.
        Assert.Single(saved.GetProperty("preferences").EnumerateArray());
        Assert.Single(saved.GetProperty("allergens").EnumerateArray());
        Assert.True(saved.GetProperty("shouldShowOnboarding").GetBoolean());
        var complete = new {
            genderCode = "FEMALE", dateOfBirth = "1995-01-01", heightCm = 165, weightKg = 70,
            goalCode = "LOSE_WEIGHT", dailyRoutineCode = "OFFICE_WORK", activityLevelCode = "MODERATE",
            onboardingStatus = "COMPLETED", preferencesConfirmed = true, allergensConfirmed = true,
            preferences = Array.Empty<object>(), allergens = Array.Empty<object>()
        };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var response = await client.PutAsJsonAsync("/api/v1/customer/profile", complete);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            Assert.True(json.GetProperty("isQuestionnaireComplete").GetBoolean());
            Assert.Empty(json.GetProperty("preferences").EnumerateArray());
            Assert.Empty(json.GetProperty("allergens").EnumerateArray());
            Assert.Equal("PROFILE_COMPLETED", json.GetProperty("nextStepCode").GetString());
            Assert.Equal(order.CustomerProfileId, json.GetProperty("id").GetGuid());
        }
        Assert.Equal(1, await db.CustomerProfiles.CountAsync(x => x.UserId == userId));
        var loaded = await client.GetFromJsonAsync<JsonElement>("/api/v1/customer/profile");
        Assert.True(loaded.GetProperty("data").GetProperty("isQuestionnaireComplete").GetBoolean());
    }

    [UpcomingPostgresFact]
    public async Task Incomplete_questionnaire_blocks_new_orders_but_not_public_browsing_or_existing_orders()
    {
        var profile = await db.CustomerProfiles.SingleAsync();
        profile.OnboardingStatus = "IN_PROGRESS";
        profile.GoalCode = null;
        await db.SaveChangesAsync();
        await using var factory = new ApiFactory(connection);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/customer/meal-plans/BALANCE/purchase-options")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Development-User-Id", userId.ToString());
        var price = await db.MealPlanPrices.SingleAsync();
        var branch = new DietTime.Domain.TakeawayBranch { Id = Guid.NewGuid(), Name = "Test branch", IsActive = true };
        db.Add(branch); await db.SaveChangesAsync();
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders") {
            Content = JsonContent.Create(new {
                customerProfileId = profile.Id, mealPlanTemplateId = price.MealPlanTemplateId,
                mealPlanPriceId = price.Id, fulfillmentMethod = "TAKEAWAY", takeawayBranchId = branch.Id,
                startDate = "2026-11-01", deliveryDays = new[] { 1, 2, 3, 4, 6, 7 }
            })
        };
        message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(message);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("questionnaire_incomplete", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/orders/{order.Id}")).StatusCode);
        Assert.Equal(1, await db.Orders.CountAsync());
    }
}
