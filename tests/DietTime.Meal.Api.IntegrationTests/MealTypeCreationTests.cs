using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using DietTime.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Meal_type_creation_saves_translations_and_refreshes_public_lookups()
    {
        var setup = await ApprovalClient();
        await using var factory = setup.Factory;
        using var client = setup.Client;
        await client.GetAsync("/api/v1/meal-types?language=en");
        await client.GetAsync("/api/v1/meal-types?language=ar");
        // Match the SPA's create payload, including its omitted display order.
        var response = await client.PostAsJsonAsync("/api/v1/admin/meal-types", new
        {
            code = "meal5", nameEn = " PM Meal ", nameAr = " وجبة مسائية ", isActive = true
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("id").GetGuid();
        var saved = await db.MealTypes.AsNoTracking().Include(t => t.Translations).SingleAsync(t => t.Id == id);
        Assert.Equal("MEAL5", saved.Code);
        Assert.True(saved.IsActive);
        Assert.Equal(0, saved.DisplayOrder);
        Assert.Equal(userId, saved.CreatedBy);
        Assert.Equal("PM Meal", saved.Translations.Single(t => t.LanguageCode == "en").Name);
        Assert.Equal("وجبة مسائية", saved.Translations.Single(t => t.LanguageCode == "ar").Name);
        foreach (var (language, expected) in new[] { ("en", "PM Meal"), ("ar", "وجبة مسائية") })
        {
            var lookup = await client.GetFromJsonAsync<JsonElement>($"/api/v1/meal-types?language={language}");
            Assert.Equal(expected, lookup.GetProperty("data").EnumerateArray().Single(t => t.TryGetProperty("id", out var value) && value.GetGuid() == id).GetProperty("name").GetString());
        }
        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/admin/meal-types?search=MEAL5");
        Assert.Equal("PM Meal", Assert.Single(list.GetProperty("data").EnumerateArray()).GetProperty("nameEn").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/v1/admin/meal-types/{id}", new UpsertMealTypeRequest("MEAL5", "Evening Meal", 1, true, "وجبة مسائية"))).StatusCode);
    }

    [UpcomingPostgresFact]
    public async Task Meal_type_creation_rejects_duplicate_normalized_codes_without_overwriting_existing_names()
    {
        var setup = await ApprovalClient();
        await using var factory = setup.Factory;
        using var client = setup.Client;
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/admin/meal-types", new UpsertMealTypeRequest("MEAL5", "PM Meal", 0))).StatusCode);
        var duplicate = await client.PostAsJsonAsync("/api/v1/admin/meal-types", new UpsertMealTypeRequest("meal5", "Replacement", 0));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Contains("duplicate_code", await duplicate.Content.ReadAsStringAsync());
        Assert.Equal(1, await db.MealTypes.CountAsync(t => t.Code == "MEAL5"));
        Assert.Equal("PM Meal", await db.MealTypes.Where(t => t.Code == "MEAL5").SelectMany(t => t.Translations).Select(t => t.Name).SingleAsync());
    }

    [UpcomingPostgresFact]
    public async Task Meal_type_creation_validates_existing_schema_limits_and_requires_authentication()
    {
        var setup = await ApprovalClient();
        await using var factory = setup.Factory;
        using var client = setup.Client;
        var initialCount = await db.MealTypes.CountAsync();
        var requests = new[]
        {
            new UpsertMealTypeRequest("BAD CODE", "Name", 0),
            new UpsertMealTypeRequest("MEAL5", "", 0),
            new UpsertMealTypeRequest("MEAL5", new string('x', 101), 0),
            new UpsertMealTypeRequest("MEAL5", "Name", 0, true, new string('x', 101)),
            new UpsertMealTypeRequest("MEAL5", "Name", -1)
        };
        foreach (var request in requests)
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/admin/meal-types", request)).StatusCode);
        Assert.Equal(initialCount, await db.MealTypes.CountAsync());
        client.DefaultRequestHeaders.Remove("X-Development-User-Id");
        // Development requests without a bearer header intentionally receive a local
        // admin identity. An invalid token must instead use real JWT authentication.
        client.DefaultRequestHeaders.Authorization = new("Bearer", "invalid-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/admin/meal-types", new UpsertMealTypeRequest("MEAL5", "Name", 0))).StatusCode);
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            "DietTime.Tests", "DietTime.Tests", [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, "Customer")],
            expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes("test-only-key-at-least-thirty-two-characters-long")), SecurityAlgorithms.HmacSha256)));
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/admin/meal-types", new UpsertMealTypeRequest("MEAL5", "Name", 0))).StatusCode);
    }
}
