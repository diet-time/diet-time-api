using DietTime.Application;
using DietTime.Domain;

namespace DietTime.UnitTests;

public sealed class AutomaticMealAssignmentTests
{
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void Packages_use_distinct_configured_meal_types_and_separate_snacks(int count)
    {
        var types = Enumerable.Range(1, 5).Select(i => Type($"CONFIGURED_{i}", i * 10)).ToArray();
        var options = types.SelectMany(t => new[] { Option(t, true), Option(t, false) }).ToList();
        var snack = Option(Type("SNACK", 0), true);
        options.Add(snack);
        var result = AutomaticMealAssignmentRules.Select(options.AsEnumerable().Reverse().ToArray(), count, 1, _ => 0);
        Assert.Null(result.ReviewReason);
        Assert.Equal(types.Take(count).Select(t => t.Id), result.Meals.Select(o => o.Slot.MealTypeId));
        Assert.Equal(count, result.Meals.Select(o => o.Slot.MealTypeId).Distinct().Count());
        Assert.All(result.Meals, o => Assert.True(o.IsDefault));
        Assert.Equal(snack.Id, Assert.Single(result.Snacks).Id);
    }

    [Fact]
    public void Multiple_slots_with_the_same_type_do_not_count_as_distinct_categories()
    {
        var type = Type("CUSTOM", 10);
        var result = AutomaticMealAssignmentRules.Select([Option(type, true), Option(type, true)], 2, 0, _ => 0);
        Assert.Single(result.Meals);
        Assert.Contains("2 distinct eligible meal types", result.ReviewReason);
        Assert.Contains("only 1", result.ReviewReason);
    }

    [Fact]
    public void Snack_quantity_is_independent_of_the_one_per_meal_type_limit()
    {
        var meal = Option(Type("CUSTOM", 10), true);
        var snack = Type("SNACK_DESSERT", 0);
        var result = AutomaticMealAssignmentRules.Select([meal, Option(snack, true), Option(snack, false)], 1, 2, _ => 0);
        Assert.Null(result.ReviewReason);
        Assert.Single(result.Meals);
        Assert.Equal(2, result.Snacks.Count);
    }

    [Fact]
    public void Existing_preference_and_default_priority_is_used_within_each_category()
    {
        var type = Type("CUSTOM", 10);
        var configuredDefault = Option(type, true, 50);
        var alternative = Option(type, false, 0);
        var options = new[] { alternative, configuredDefault };
        Assert.Equal(configuredDefault.Id, Assert.Single(AutomaticMealAssignmentRules.Select(options, 1, 0, _ => 0).Meals).Id);
        Assert.Equal(alternative.Id, Assert.Single(AutomaticMealAssignmentRules.Select(options, 1, 0,
            o => o.Id == alternative.Id ? 100 : 0).Meals).Id);
        Assert.Equal(alternative.Id, Assert.Single(AutomaticMealAssignmentRules.Select(options, 1, 0,
            _ => 0, new HashSet<Guid> { alternative.MealItemId }).Meals).Id);
    }

    [Fact]
    public void Missing_snacks_produce_a_review_reason_and_preserve_valid_meals()
    {
        var meal = Option(Type("CUSTOM", 1), true);
        var result = AutomaticMealAssignmentRules.Select([meal], 1, 1, _ => 0);
        Assert.Equal(meal.Id, Assert.Single(result.Meals).Id);
        Assert.Empty(result.Snacks);
        Assert.Contains("requires 1 eligible snacks", result.ReviewReason);
    }

    [Fact]
    public void Repeated_selection_is_deterministic_and_does_not_mutate_input()
    {
        var types = Enumerable.Range(1, 3).Select(i => Type($"CONFIGURED_{i}", i)).ToArray();
        var options = types.SelectMany(t => new[] { Option(t, true), Option(t, false) }).ToArray();
        var before = options.Select(o => (o.Id, o.MealItemId, o.IsDefault)).ToArray();
        var first = AutomaticMealAssignmentRules.Select(options, 3, 0, _ => 0);
        var second = AutomaticMealAssignmentRules.Select(options.Reverse().ToArray(), 3, 0, _ => 0);
        Assert.Equal(first.Meals.Select(o => o.Id), second.Meals.Select(o => o.Id));
        Assert.Equal(before, options.Select(o => (o.Id, o.MealItemId, o.IsDefault)).ToArray());
    }

    [Fact]
    public void Shared_default_dish_uses_a_ranked_alternative_when_needed_to_fill_distinct_types()
    {
        var first = Type("CUSTOM_A", 10);
        var second = Type("CUSTOM_B", 20);
        var shared = Option(first, true);
        var alternative = Option(first, false);
        var onlySecond = Option(second, true);
        onlySecond.MealItemId = shared.MealItemId;
        var result = AutomaticMealAssignmentRules.Select([shared, alternative, onlySecond], 2, 0, _ => 0);
        Assert.Null(result.ReviewReason);
        Assert.Equal(new[] { alternative.Id, onlySecond.Id }, result.Meals.Select(o => o.Id));
    }
    private static MealType Type(string code, int order) => new() { Id = Guid.NewGuid(), Code = code, DisplayOrder = order };
    private static MealPlanSlotOption Option(MealType type, bool isDefault, int order = 0) => new()
    {
        Id = Guid.NewGuid(), MealItemId = Guid.NewGuid(), IsDefault = isDefault, DisplayOrder = order,
        Slot = new MealPlanTemplateSlot { Id = Guid.NewGuid(), MealType = type, MealTypeId = type.Id }
    };
}