using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Dietary_preferences_do_not_filter_available_dishes_or_block_confirmation()
    {
        foreach (var code in new[] { "INTERNATIONAL", "MEDITERRANEAN", "VEGAN", "VEGETARIAN", "GLUTEN_FREE", "DAIRY_FREE", "NUT_FREE", "NO_SPICY", "UNKNOWN_RESTRICTION" })
            db.Add(new CustomerProfilePreference { Id = Guid.NewGuid(), CustomerProfileId = order.CustomerProfileId, PreferenceCode = code, PreferenceType = "DIET_STYLE" });
        await db.SaveChangesAsync();
        var initial = await PrepareAsync();
        var dayId = initial.SubscriptionServiceDayId!.Value;
        // Reproduce an existing order that was provisionally assigned no dishes.
        await db.Set<CustomerMealSelection>().Where(s => s.SubscriptionServiceDayId == dayId).ExecuteDeleteAsync();
        db.ChangeTracker.Clear();
        var day = await db.Set<SubscriptionServiceDay>().SingleAsync(d => d.Id == dayId);
        day.ReviewReason = "Not enough eligible meals are configured for this delivery day. Our team needs to review it.";
        day.SelectionStatus = "PENDING";
        await db.SaveChangesAsync();

        var refreshed = (await Service().GetTomorrowAsync(order.Id, userId, "en", default))!;
        Assert.Empty(refreshed.SelectedMeals);
        Assert.Equal(5, refreshed.AvailableMeals.Count);
        Assert.Equal(2, refreshed.AvailableSnacks.Count);
        Assert.True(refreshed.CanConfirm);
        var choices = new ConfirmMealsRequest(refreshed.AvailableMeals.Take(3).Select(m => m.MealItemId).ToArray(),
            refreshed.AvailableSnacks.Take(1).Select(m => m.MealItemId).ToArray());
        var confirmed = await Service().ConfirmAsync(order.Id, dayId, userId, "en", choices, default);
        Assert.Equal("CUSTOMER_CONFIRMED", confirmed.SelectionStatus);
        Assert.Null(confirmed.ReviewReason);
        Assert.Equal(4, confirmed.SelectedMeals.Count);
    }
}
