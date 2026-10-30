using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Order_counts_use_purchased_quantities_and_type_codes_not_localized_names_or_current_prices()
    {
        // Deliberately make the localized snapshot names ambiguous, including snacks.
        foreach (var meal in await db.OrderMeals.Where(x => x.OrderId == order.Id).ToArrayAsync())
            meal.MealTypeName = "وجبة مخصصة";
        var price = await db.MealPlanPrices.SingleAsync();
        price.MealsPerDay = 5;
        price.SnacksPerDay = 3;
        await db.SaveChangesAsync();
        await using var factory = new ApiFactory(connection);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Development-User-Id", userId.ToString());
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{order.Id}");
        Assert.Equal(3, detail.GetProperty("mealCount").GetInt32());
        Assert.Equal(1, detail.GetProperty("snackCount").GetInt32());
        var summary = await client.GetFromJsonAsync<JsonElement>($"/api/v1/customer-profiles/{order.CustomerProfileId}/orders");
        var item = summary.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == order.Id);
        Assert.Equal(3, item.GetProperty("mealCount").GetInt32());
        Assert.Equal(1, item.GetProperty("snackCount").GetInt32());
        Assert.Equal(2, detail.GetProperty("meals").GetArrayLength()); // Internal IDs/rows remain intact (three meals + one snack).
    }
}
