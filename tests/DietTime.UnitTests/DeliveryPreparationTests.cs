using DietTime.Application;

namespace DietTime.UnitTests;

public sealed class DeliveryPreparationTests
{
    [Fact]
    public void Empty_day_returns_no_deliveries()
    {
        var result = DeliveryPreparationAggregation.Build(new(2026, 8, 14), [], []);

        Assert.Equal("NoDeliveries", result.Status);
        Assert.Equal(0, result.OrderCount);
        Assert.Equal(0, result.CustomerCount);
        Assert.Equal(0, result.MealItemCount);
        Assert.Empty(result.MealTypes);
        Assert.Empty(result.PlanBreakdown);
    }

    [Fact]
    public void Aggregates_items_customers_plans_and_uses_stable_sorting()
    {
        var customer = Guid.NewGuid();
        var otherCustomer = Guid.NewGuid();
        var planA = Guid.NewGuid();
        var planB = Guid.NewGuid();
        var order1 = Guid.NewGuid();
        var order2 = Guid.NewGuid();
        var order3 = Guid.NewGuid();
        var breakfast = Guid.NewGuid();
        var lunch = Guid.NewGuid();
        var wrap = Guid.NewGuid();
        var croissant = Guid.NewGuid();
        var chicken = Guid.NewGuid();
        var orders = new[]
        {
            new DeliveryPreparationOrderSource(order1, customer, planA, "Everyday Choice"),
            new DeliveryPreparationOrderSource(order2, customer, planA, "Everyday Choice"),
            new DeliveryPreparationOrderSource(order3, otherCustomer, planB, "Balanced Living")
        };
        var rows = new[]
        {
            new DeliveryPreparationMenuSource(order1, lunch, "Lunch", 2, chicken, "Grilled Chicken", 2),
            new DeliveryPreparationMenuSource(order1, breakfast, "Breakfast", 1, wrap, "Chicken Wrap", 1),
            new DeliveryPreparationMenuSource(order2, breakfast, "Breakfast", 1, wrap, "Chicken Wrap", 2),
            new DeliveryPreparationMenuSource(order3, breakfast, "Breakfast", 1, croissant, "Egg Croissant", 2)
        };

        var result = DeliveryPreparationAggregation.Build(new(2026, 8, 15), orders, rows);

        Assert.Equal("Scheduled", result.Status);
        Assert.Equal(3, result.OrderCount);
        Assert.Equal(2, result.CustomerCount);
        Assert.Equal(7, result.MealItemCount);
        Assert.Equal(new[] { "Breakfast", "Lunch" }, result.MealTypes.Select(type => type.MealTypeName));
        var breakfastGroup = result.MealTypes[0];
        Assert.Equal(5, breakfastGroup.Quantity);
        Assert.Equal(breakfastGroup.Quantity, breakfastGroup.Items.Sum(item => item.Quantity));
        Assert.Equal("Chicken Wrap", breakfastGroup.Items[0].MenuItemName);
        Assert.Equal(3, breakfastGroup.Items[0].Quantity);
        Assert.Equal(2, result.PlanBreakdown[0].OrderCount);
        Assert.Equal("Everyday Choice", result.PlanBreakdown[0].MealPlanName);
    }

    [Fact]
    public void Menu_items_sort_by_quantity_then_name()
    {
        var orderId = Guid.NewGuid();
        var mealTypeId = Guid.NewGuid();
        var rows = new[]
        {
            new DeliveryPreparationMenuSource(orderId, mealTypeId, "Lunch", 1, Guid.NewGuid(), "Zulu", 1),
            new DeliveryPreparationMenuSource(orderId, mealTypeId, "Lunch", 1, Guid.NewGuid(), "Alpha", 1),
            new DeliveryPreparationMenuSource(orderId, mealTypeId, "Lunch", 1, Guid.NewGuid(), "Popular", 3)
        };

        var result = DeliveryPreparationAggregation.Build(
            new(2026, 8, 15),
            [new(orderId, Guid.NewGuid(), Guid.NewGuid(), "Plan")],
            rows);

        Assert.Equal(new[] { "Popular", "Alpha", "Zulu" },
            result.MealTypes[0].Items.Select(item => item.MenuItemName));
    }
}
