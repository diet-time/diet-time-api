using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed class CustomerAddressApiTests : IAsyncLifetime
{
    private readonly bool enabled = Environment.GetEnvironmentVariable("RUN_INTEGRATION_TESTS") == "true";
    private PostgreSqlContainer? postgres;
    private ApiFactory? factory;
    private HttpClient? client;
    private Guid userId;
    private Guid otherUserId;
    private Guid profileId;

    public async Task InitializeAsync()
    {
        if (!enabled) return;
        postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("diettime_addresses_test")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
        await postgres.StartAsync();
        factory = new ApiFactory(postgres.GetConnectionString());
        client = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DietTimeDbContext>();
        await db.Database.EnsureCreatedAsync();
        userId = Guid.NewGuid();
        otherUserId = Guid.NewGuid();
        profileId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.Users.AddRange(
            new ApplicationUser { Id = userId, UserName = "address-owner", SecurityStamp = Guid.NewGuid().ToString() },
            new ApplicationUser { Id = otherUserId, UserName = "other-address-user", SecurityStamp = Guid.NewGuid().ToString() });
        db.CustomerProfiles.Add(new CustomerProfile
        {
            Id = profileId, UserId = userId, IsActive = true, PreferredLanguage = "en",
            OnboardingStatus = "IN_PROGRESS", CreatedAt = now, UpdatedAt = now, RowVersion = 1
        });
        db.DeliveryTimeSlots.AddRange(
            new DeliveryTimeSlot { Id = Guid.NewGuid(), Code = "EVENING", Name = "Evening", NameAr = "مساءً", StartTime = new(19, 0), EndTime = new(20, 15), SortOrder = 2, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new DeliveryTimeSlot { Id = Guid.NewGuid(), Code = "MORNING", Name = "Morning", NameAr = "صباحاً", StartTime = new(9, 0), EndTime = new(11, 0), SortOrder = 1, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new DeliveryTimeSlot { Id = Guid.NewGuid(), Code = "OLD", Name = "Old", NameAr = "قديم", StartTime = new(12, 0), EndTime = new(13, 0), SortOrder = 0, IsActive = false, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        Authenticate(userId);
    }

    public async Task DisposeAsync()
    {
        client?.Dispose();
        if (factory is not null) await factory.DisposeAsync();
        if (postgres is not null) await postgres.DisposeAsync();
    }

    [Fact]
    public async Task Manages_default_lifecycle_and_soft_deletes()
    {
        if (!enabled) return;
        var http = client!;
        var firstResponse = await http.PostAsJsonAsync(Route(), Address("Home", false));
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        var first = await firstResponse.Content.ReadFromJsonAsync<JsonElement>();
        var firstId = first.GetProperty("id").GetGuid();
        Assert.True(first.GetProperty("isDefault").GetBoolean());

        var secondResponse = await http.PostAsJsonAsync(Route(), Address("Office", true, "OFFICE"));
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
        var second = await secondResponse.Content.ReadFromJsonAsync<JsonElement>();
        var secondId = second.GetProperty("id").GetGuid();

        var list = await http.GetFromJsonAsync<JsonElement>(Route());
        var items = list.GetProperty("items");
        Assert.Equal(secondId, items[0].GetProperty("id").GetGuid());
        Assert.True(items[0].GetProperty("isDefault").GetBoolean());
        Assert.False(items[1].GetProperty("isDefault").GetBoolean());

        var deleted = await http.DeleteAsync($"{Route()}/{secondId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var remaining = (await http.GetFromJsonAsync<JsonElement>(Route())).GetProperty("items");
        Assert.Single(remaining.EnumerateArray());
        Assert.Equal(firstId, remaining[0].GetProperty("id").GetGuid());
        Assert.True(remaining[0].GetProperty("isDefault").GetBoolean());

        using var scope = factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DietTimeDbContext>();
        var removed = await db.CustomerAddresses.SingleAsync(x => x.Id == secondId);
        Assert.False(removed.IsActive);
        Assert.False(removed.IsDefault);
    }

    [Fact]
    public async Task Rejects_invalid_input_and_foreign_profile_access()
    {
        if (!enabled) return;
        var invalid = await client!.PostAsJsonAsync(Route(), new
        {
            addressType = "VILLA", area = "", latitude = 91, longitude = 181
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType?.MediaType);

        Authenticate(otherUserId);
        var foreign = await client!.GetAsync(Route());
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Fact]
    public async Task Returns_only_active_delivery_slots_in_sort_order()
    {
        if (!enabled) return;
        var response = await client!.GetAsync("/api/v1/delivery-time-slots");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = json.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("MORNING", items[0].GetProperty("code").GetString());
        Assert.Equal("09:00:00", items[0].GetProperty("startTime").GetString());
        Assert.Equal("EVENING", items[1].GetProperty("code").GetString());
    }

    private string Route() => $"/api/v1/customer-profiles/{profileId}/addresses";
    private static object Address(string name, bool isDefault, string type = "HOME") => new
    {
        addressName = name, addressType = type, buildingNo = "A-126", streetNo = "960",
        zoneNo = "91", area = "Al Wakrah", latitude = 25.1712345m,
        longitude = 51.6034567m, formattedAddress = "Zone 91, Al Wakrah, Qatar", isDefault
    };

    private void Authenticate(Guid id)
    {
        client!.DefaultRequestHeaders.Remove("X-Development-User-Id");
        client.DefaultRequestHeaders.Add("X-Development-User-Id", id.ToString());
    }
}
