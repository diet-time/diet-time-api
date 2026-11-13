using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Profile_edit_retains_existing_inactive_allergies_but_rejects_new_inactive_and_unknown_ids()
    {
        var recorded = new Allergen { Id = Guid.NewGuid(), Code = "RECORDED", IsActive = false };
        var unselected = new Allergen { Id = Guid.NewGuid(), Code = "UNSELECTED", IsActive = false };
        var active = new Allergen { Id = Guid.NewGuid(), Code = "ACTIVE", IsActive = true };
        db.AddRange(recorded, unselected, active,
            new CustomerProfileAllergen { Id = Guid.NewGuid(), CustomerProfileId = order.CustomerProfileId, AllergenId = recorded.Id });
        await db.SaveChangesAsync();
        await using var factory = new ApiFactory(connection);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Development-User-Id", userId.ToString());

        var loaded = (await client.GetFromJsonAsync<JsonElement>("/api/v1/customer/profile")).GetProperty("data");
        var allergens = loaded.GetProperty("allergens").EnumerateArray()
            .Select(a => new { allergenId = a.GetProperty("allergenId").GetGuid() }).ToArray();
        Assert.Equal(recorded.Id, Assert.Single(allergens).allergenId);
        var saved = await client.PutAsJsonAsync("/api/v1/customer/profile", new { weightKg = 73, allergens });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(recorded.Id, (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data")
            .GetProperty("allergens")[0].GetProperty("allergenId").GetGuid());

        foreach (var invalidId in new[] { unselected.Id, Guid.NewGuid() })
        {
            var rejected = await client.PutAsJsonAsync("/api/v1/customer/profile", new {
                weightKg = 80, allergens = new[] { new { allergenId = recorded.Id }, new { allergenId = invalidId } }
            });
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Contains("invalid_allergen_id", await rejected.Content.ReadAsStringAsync());
            await using var verify = Context();
            Assert.Equal(73m, (await verify.CustomerProfiles.SingleAsync(p => p.Id == order.CustomerProfileId)).WeightKg);
            Assert.Equal(recorded.Id, (await verify.CustomerProfileAllergens.SingleAsync()).AllergenId);
        }

        var added = await client.PutAsJsonAsync("/api/v1/customer/profile", new {
            allergens = new[] { new { allergenId = recorded.Id }, new { allergenId = active.Id } }
        });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        Assert.Equal(2, (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("allergens").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/v1/customer/profile", new { allergens = Array.Empty<object>() })).StatusCode);
        await using var removed = Context();
        Assert.Empty(await removed.CustomerProfileAllergens.ToArrayAsync());
        // Once removed, an inactive allergen cannot be selected again.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/v1/customer/profile", new { allergens })).StatusCode);
    }
}
