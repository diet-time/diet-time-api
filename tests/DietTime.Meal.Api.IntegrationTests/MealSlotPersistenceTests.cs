using System.Reflection;
using DietTime.Contracts;
using DietTime.Domain;
using DietTime.Persistence;

namespace DietTime.Meal.Api.IntegrationTests;

// These graph/DTO tests run without a database or Docker. HTTP/database coverage
// still needs an isolated PostgreSQL instance and the deployment comparison.
public sealed class MealSlotPersistenceTests
{
    [Fact]
    public void Removing_slot_preserves_slot_options_and_other_weeks()
    {
        var plan = Plan();
        var day = plan.Days.First();
        var slot = day.Slots.Single();
        var option = slot.Options.Single();
        var otherWeek = plan.Days.Last();
        Reconcile(plan, [new(MenuWeekday.Thursday, 6, true, [], 1)], 1);
        Assert.Same(slot, day.Slots.Single());
        Assert.False(slot.IsActive);
        Assert.Same(option, slot.Options.Single());
        Assert.True(otherWeek.IsActive);
        Assert.True(otherWeek.Slots.Single().IsActive);
    }

    [Fact]
    public void Removing_day_deactivates_it_without_deleting_dependencies()
    {
        var plan = Plan();
        Reconcile(plan, [], null);
        Assert.Equal(2, plan.Days.Count);
        Assert.All(plan.Days, day => {
            Assert.False(day.IsActive);
            Assert.Single(day.Slots.Single().Options);
        });
    }

    [Fact]
    public void Updating_options_retains_existing_ids_and_disables_removed_option()
    {
        var plan = Plan();
        var day = plan.Days.First();
        var slot = day.Slots.Single();
        var removed = slot.Options.Single();
        var kept = new MealPlanSlotOption { Id = Guid.NewGuid(), MealItemId = Guid.NewGuid(), IsAvailable = true };
        slot.Options.Add(kept);
        Reconcile(plan, [new(MenuWeekday.Thursday, 6, true,
            [new(slot.MealTypeId, 4, 1, 1, true, null, false,
                [new(kept.MealItemId, 2m, true, true, 1)], true)], 1)], 1);
        Assert.Same(slot, day.Slots.Single());
        Assert.Contains(kept, slot.Options);
        Assert.False(removed.IsAvailable);
        Assert.False(removed.IsDefault);
        Assert.True(kept.IsAvailable);
        Assert.Equal(2m, kept.AdditionalPrice);
    }

    [Fact]
    public void Active_template_day_projection_excludes_inactive_slot_and_meal_type()
    {
        var plan = Plan();
        var day = plan.Days.First();
        var slot = day.Slots.Single();
        slot.MealType = new MealType { Code = "MEAL4", IsActive = true };
        slot.IsActive = false;
        var projection = typeof(AdminMealService).GetMethod("ToTemplateDayDetail", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (MealPlanTemplateDayDetailResponse)projection.Invoke(null, [day])!;
        Assert.Empty(result.Slots);
        Assert.Equal(0, result.SlotCount);
        slot.IsActive = true;
        slot.MealType.IsActive = false;
        result = (MealPlanTemplateDayDetailResponse)projection.Invoke(null, [day])!;
        Assert.Empty(result.Slots);
    }

    private static void Reconcile(MealPlanTemplate plan, UpsertPlanDayRequest[] days, int? week) =>
        typeof(AdminMealService).GetMethod("ReconcilePlanStructure", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [plan, days, week, DateTimeOffset.UtcNow, null]);

    private static MealPlanTemplate Plan() => new() {
        Days = new[] { 1, 2 }.Select(week => new MealPlanTemplateDay {
            Id = Guid.NewGuid(), WeekNumber = week, MenuWeekday = MenuWeekday.Thursday, IsActive = true,
            Slots = [new() { Id = Guid.NewGuid(), MealTypeId = Guid.NewGuid(), IsActive = true,
                Options = [new() { Id = Guid.NewGuid(), MealItemId = Guid.NewGuid(), IsDefault = true, IsAvailable = true }] }]
        }).ToList()
    };
}
