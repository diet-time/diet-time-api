using DietTime.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Text.Json;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Tomorrow_HTTP_and_confirmation_return_purchased_plan_image_and_order_metadata()
    {
        var menu = await PrepareAsync();
        db.MealMedia.Add(new() { EntityId = order.MealPlanTemplateId, MediaType = MealMediaTypes.MealPlan,
            ObjectKey = "plans/balance.jpg", PublicUrl = "https://cdn.test/balance.jpg",
            ThumbnailUrl = "https://cdn.test/balance-thumb.jpg", IsPrimary = true, Status = "ACTIVE",
            CreatedAt = clock.Now, UpdatedAt = clock.Now });
        await db.SaveChangesAsync();
        await using var factory = new ApiFactory(connection, services => services.AddSingleton<TimeProvider>(clock));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Development-User-Id", userId.ToString());
        var payload = (await client.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{order.Id}/tomorrow-menu")).GetProperty("data");
        Assert.Equal(order.Id, payload.GetProperty("orderId").GetGuid());
        Assert.Equal(order.OrderNumber, payload.GetProperty("orderNumber").GetString());
        Assert.Equal(order.MealPlanTemplateId, payload.GetProperty("mealPlanTemplateId").GetGuid());
        Assert.Equal("https://cdn.test/balance-thumb.jpg", payload.GetProperty("mealPlanImageUrl").GetString());
        Assert.Equal(menu.SubscriptionServiceDayId, payload.GetProperty("subscriptionServiceDayId").GetGuid());
        Assert.Equal(3, payload.GetProperty("selectedMealCount").GetInt32());
        Assert.Equal(1, payload.GetProperty("selectedSnackCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("customerConfirmedAt").ValueKind);
        var confirmed = await Service().ConfirmAsync(order.Id, menu.SubscriptionServiceDayId!.Value, userId, "en", Request(menu), default);
        Assert.Equal("https://cdn.test/balance-thumb.jpg", confirmed.MealPlanImageUrl);
        Assert.Equal(order.OrderNumber, confirmed.OrderNumber);
        Assert.NotNull(confirmed.CustomerConfirmedAt);
        Assert.True(confirmed.SelectionLocked);
    }

    [UpcomingPostgresFact]
    public async Task Tomorrow_keeps_purchased_plan_image_when_a_newer_version_is_published()
    {
        await PrepareAsync();
        var purchased = await db.MealPlanTemplates.SingleAsync(plan => plan.Id == order.MealPlanTemplateId);
        var newer = new MealPlanTemplate { Id = Guid.NewGuid(), VersionGroupId = purchased.VersionGroupId,
            VersionNumber = purchased.VersionNumber + 1, Code = "FUTURE_IMAGE_TEST", IsPublished = true,
            IsActive = true, CreatedAt = clock.Now, UpdatedAt = clock.Now };
        db.Add(newer);
        db.MealMedia.Add(new() { EntityId = newer.Id, MediaType = MealMediaTypes.MealPlan,
            ObjectKey = "future.jpg", PublicUrl = "https://cdn.test/future.jpg", IsPrimary = true,
            Status = "ACTIVE", CreatedAt = clock.Now, UpdatedAt = clock.Now });
        await db.SaveChangesAsync();
        var missing = (await Service().GetTomorrowAsync(order.Id, userId, "en", default))!;
        Assert.Null(missing.MealPlanImageUrl); // Never substitute a future version or a menu-item image.
        db.MealMedia.Add(new() { EntityId = purchased.Id, MediaType = MealMediaTypes.MealPlan,
            ObjectKey = "purchased.jpg", PublicUrl = "https://cdn.test/purchased.jpg", IsPrimary = true,
            Status = "ACTIVE", CreatedAt = clock.Now, UpdatedAt = clock.Now });
        await db.SaveChangesAsync();
        var saved = (await Service().GetTomorrowAsync(order.Id, userId, "en", default))!;
        Assert.Equal(purchased.Id, saved.MealPlanTemplateId);
        Assert.Equal(purchased.VersionNumber, saved.MealPlanVersion);
        Assert.Equal("https://cdn.test/purchased.jpg", saved.MealPlanImageUrl);
    }
}
