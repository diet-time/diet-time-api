using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DietTime.Contracts;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Address_field_rules_return_structured_errors_and_preserve_saved_legacy_addresses()
    {
        var slot = new DeliveryTimeSlot { Id = Guid.NewGuid(), Code = "MORNING", Name = "Morning", NameAr = "Morning", StartTime = new(7,0), EndTime = new(11,0), IsActive = true };
        var legacy = new CustomerAddress { Id = Guid.NewGuid(), CustomerProfileId = order.CustomerProfileId, Area = "Legacy Area 99", IsActive = true };
        db.AddRange(slot, legacy); await db.SaveChangesAsync();
        await using var factory = new ApiFactory(connection, services => services.AddSingleton<TimeProvider>(clock));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Development-User-Id", userId.ToString());
        var path = $"/api/v1/customer-profiles/{order.CustomerProfileId}/addresses";
        var request = new UpsertCustomerAddressRequest("Home", "HOME", "A-126/B", "960", null, "91", " الوكرة ",
            "Gate 3: call +974 12345678!", 25.2m, 51.5m, "Doha", false, slot.Id);
        var invalid = await client.PostAsJsonAsync(path, request with { BuildingNo = "", Area = "Al Wakrah2", ZoneNo = "A91" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("BuildingNo", out _));
        Assert.True(errors.TryGetProperty("Area", out _));
        Assert.True(errors.TryGetProperty("ZoneNo", out _));
        var created = await client.PostAsJsonAsync(path, request);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var saved = (await created.Content.ReadFromJsonAsync<CustomerAddressResponse>())!;
        Assert.Equal("الوكرة", saved.Area); Assert.Equal("A-126/B", saved.BuildingNo); Assert.Equal("960", saved.StreetNo); Assert.Null(saved.UnitNumber);
        var edited = await client.PutAsJsonAsync($"{path}/{saved.Id}", request with { Area = "West Bay", UnitNumber = "B/12" });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var rejected = await client.PutAsJsonAsync($"{path}/{saved.Id}", request with { Area = "الوكرة٢" });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var retained = (await client.GetFromJsonAsync<CustomerAddressResponse>($"{path}/{saved.Id}"))!;
        Assert.Equal("West Bay", retained.Area); Assert.Equal("B/12", retained.UnitNumber);
        var oldAddress = (await client.GetFromJsonAsync<CustomerAddressResponse>($"{path}/{legacy.Id}"))!;
        Assert.Equal("Legacy Area 99", oldAddress.Area); Assert.Null(oldAddress.BuildingNo); Assert.Null(oldAddress.StreetNo); Assert.Null(oldAddress.ZoneNo);
        var legacyEdit = await client.PutAsJsonAsync($"{path}/{legacy.Id}", request with { BuildingNo = null, StreetNo = null, ZoneNo = null, Area = oldAddress.Area });
        Assert.True(legacyEdit.StatusCode == HttpStatusCode.OK, await legacyEdit.Content.ReadAsStringAsync());
        var legacyRetained = (await client.GetFromJsonAsync<CustomerAddressResponse>($"{path}/{legacy.Id}"))!;
        Assert.Equal("Legacy Area 99", legacyRetained.Area); Assert.Null(legacyRetained.BuildingNo); Assert.Equal(slot.Id, legacyRetained.DeliveryTimeSlotId);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{path}/{legacy.Id}", request with { Area = "Changed Area 99" })).StatusCode);
        Assert.Equal(1, await db.Orders.CountAsync());
        Assert.Empty(await db.Set<CustomerMealSelection>().ToArrayAsync());
        Assert.Equal(1299m, await db.MealPlanPrices.Select(p => p.Amount).SingleAsync());
    }
}
