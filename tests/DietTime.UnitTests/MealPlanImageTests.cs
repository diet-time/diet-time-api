using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace DietTime.UnitTests;

public sealed class MealPlanImageTests
{
    [Fact]
    public void Purchased_version_wins_over_other_versions_and_dish_images()
    {
        var group = Guid.NewGuid();
        var old = new MealPlanTemplate { Id = Guid.NewGuid(), VersionGroupId = group, VersionNumber = 1 };
        var purchased = new MealPlanTemplate { Id = Guid.NewGuid(), VersionGroupId = group, VersionNumber = 2 };
        var future = new MealPlanTemplate { Id = Guid.NewGuid(), VersionGroupId = group, VersionNumber = 3 };
        var exact = Image(purchased.Id);
        var dish = Image(purchased.Id); dish.MediaType = MealMediaTypes.MealItem;
        var images = new[] { Image(future.Id), dish, Image(old.Id), exact }.AsQueryable();
        Assert.Same(exact, MealPlanImages.ForPurchasedPlan(images, new[] { old, purchased, future }.AsQueryable(), purchased).First());
    }

    [Fact]
    public void Missing_version_image_can_inherit_only_from_the_purchased_plan_history()
    {
        var group = Guid.NewGuid();
        var old = new MealPlanTemplate { Id = Guid.NewGuid(), VersionGroupId = group, VersionNumber = 1 };
        var purchased = new MealPlanTemplate { Id = Guid.NewGuid(), VersionGroupId = group, VersionNumber = 2 };
        var future = new MealPlanTemplate { Id = Guid.NewGuid(), VersionGroupId = group, VersionNumber = 3 };
        var unrelated = new MealPlanTemplate { Id = Guid.NewGuid(), VersionGroupId = Guid.NewGuid(), VersionNumber = 1 };
        var inactive = Image(purchased.Id); inactive.Status = "DELETED";
        var prior = Image(old.Id);
        var versions = new[] { old, purchased, future, unrelated }.AsQueryable();
        Assert.Same(prior, MealPlanImages.ForPurchasedPlan(
            new[] { inactive, Image(future.Id), Image(unrelated.Id), prior }.AsQueryable(), versions, purchased).First());
        Assert.Empty(MealPlanImages.ForPurchasedPlan(new[] { inactive, Image(future.Id), Image(unrelated.Id) }.AsQueryable(), versions, purchased));
    }

    [Fact]
    public void Purchased_plan_media_query_translates_to_postgresql_without_loading_other_plans()
    {
        using var db = new DietTimeDbContext(new DbContextOptionsBuilder<DietTimeDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .UseSnakeCaseNamingConvention().Options);
        var plan = new MealPlanTemplate { Id = Guid.NewGuid(), VersionGroupId = Guid.NewGuid(), VersionNumber = 2 };
        var sql = MealPlanImages.ForPurchasedPlan(db.MealMedia.AsNoTracking(), db.MealPlanTemplates, plan).Take(1).ToQueryString();
        Assert.Contains("meal_media", sql);
        Assert.Contains("MEALPLAN", sql);
        Assert.Contains("version_number", sql);
        Assert.Contains("LIMIT", sql);
    }

    [Theory]
    [InlineData("https://cdn.test/thumb.jpg", "thumb-key", "https://cdn.test/plan.jpg", "plan-key", "https://cdn.test/thumb.jpg")]
    [InlineData(null, "thumb-key", "https://cdn.test/plan.jpg", "plan-key", "https://storage.test/thumb-key")]
    [InlineData(null, null, "https://cdn.test/plan.jpg", "plan-key", "https://cdn.test/plan.jpg")]
    [InlineData(null, null, "/relative-plan.jpg", "plan-key", "https://storage.test/plan-key")]
    [InlineData(null, null, null, null, null)]
    public void Image_resolution_uses_pricing_conventions_and_never_returns_a_relative_url(
        string? thumbnail, string? thumbnailKey, string? original, string? key, string? expected) =>
        Assert.Equal(expected, MealPlanImages.ResolveUrl(new Storage(), thumbnail, thumbnailKey, original, key));

    [Fact]
    public void Tomorrow_contract_exposes_order_plan_image_counts_and_authoritative_confirmation()
    {
        var now = DateTimeOffset.UtcNow;
        var order = Guid.NewGuid(); var day = Guid.NewGuid(); var plan = Guid.NewGuid();
        var dto = new TomorrowMenuResponse(order, day, new(2026, 10, 29), "Complete Balance", 3, 1,
            "CUSTOMER_CONFIRMED", true, false, now, now, null, [], [], [], []) {
            OrderId = order, OrderNumber = "ORD-123", MealPlanTemplateId = plan, MealPlanVersion = 2,
            MealPlanImageUrl = "https://cdn.test/balance.jpg", SelectedMealCount = 3, SelectedSnackCount = 1,
            CustomerConfirmedAt = now
        };
        var json = JsonSerializer.SerializeToElement(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var property in new[] { "orderId", "orderNumber", "mealPlanTemplateId", "mealPlanName", "mealPlanImageUrl",
            "mealPlanVersion", "mealCount", "snackCount", "selectedMealCount", "selectedSnackCount", "selectionStatus",
            "selectionLocked", "customerConfirmedAt", "autoFinalizedAt", "cutoffAt", "subscriptionServiceDayId" })
            Assert.True(json.TryGetProperty(property, out _), property);
        Assert.Equal(order, json.GetProperty("orderId").GetGuid());
        Assert.Equal(day, json.GetProperty("subscriptionServiceDayId").GetGuid());
    }

    private static MealMedia Image(Guid id) => new() { Id = Guid.NewGuid(), EntityId = id,
        MediaType = MealMediaTypes.MealPlan, ObjectKey = "plans/balance.jpg", Status = "ACTIVE", IsPrimary = true };

    private sealed class Storage : IStorageUrlService
    {
        public long MaxUploadSizeBytes => 1024;
        public string GetPublicUrl(string key) => "https://storage.test/" + key;
        public string GetThumbnailUrl(string key) => GetPublicUrl(key);
        public Task UploadAsync(string key, Stream content, string contentType, CancellationToken ct) => Task.CompletedTask;
        public Task<StoredObject?> DownloadAsync(string key, CancellationToken ct) => Task.FromResult<StoredObject?>(null);
        public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
    }
}
