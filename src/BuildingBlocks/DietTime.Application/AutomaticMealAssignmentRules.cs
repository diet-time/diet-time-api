using DietTime.Domain;

namespace DietTime.Application;

public sealed record AutomaticMealAssignmentResult(
    IReadOnlyList<MealPlanSlotOption> Meals,
    IReadOnlyList<MealPlanSlotOption> Snacks,
    string? ReviewReason);

public static class AutomaticMealAssignmentRules
{
    // Call only with the service day's already-filtered template/week/weekday options.
    public static AutomaticMealAssignmentResult Select(
        IReadOnlyCollection<MealPlanSlotOption> eligible, int mealCount, int snackCount,
        Func<MealPlanSlotOption, decimal> preferenceScore, IReadOnlySet<Guid>? preferredMealIds = null)
    {
        var chosenDishes = new HashSet<Guid>();
        var meals = new List<MealPlanSlotOption>();
        var snacks = new List<MealPlanSlotOption>();
        IOrderedEnumerable<MealPlanSlotOption> Rank(IEnumerable<MealPlanSlotOption> options) => options
            .OrderByDescending(o => preferredMealIds?.Contains(o.MealItemId) == true)
            .ThenByDescending(preferenceScore).ThenByDescending(o => o.IsDefault)
            .ThenBy(o => o.Slot.DisplayOrder).ThenBy(o => o.DisplayOrder).ThenBy(o => o.Id);
        var mealTypes = eligible.Where(o => !MealTypeClassification.IsSnack(o.Slot.MealType.Code))
            .GroupBy(o => o.Slot.MealTypeId)
            .OrderBy(group => group.First().Slot.MealType.DisplayOrder).ThenBy(group => group.Key)
            .Select(group => Rank(group).DistinctBy(o => o.MealItemId).ToArray()).ToArray();
        var target = Math.Min(mealCount, Math.Min(mealTypes.Length,
            mealTypes.SelectMany(group => group).Select(o => o.MealItemId).Distinct().Count()));
        var best = new List<MealPlanSlotOption>();
        // Keep configured priorities unless a shared dish would prevent another
        // category from being filled. Packages require at most five meals.
        bool Fill(int typeIndex)
        {
            if (meals.Count > best.Count) best = [.. meals];
            if (meals.Count == target) return true;
            if (typeIndex == mealTypes.Length || meals.Count + mealTypes.Length - typeIndex <= best.Count) return false;
            foreach (var option in mealTypes[typeIndex])
            {
                if (!chosenDishes.Add(option.MealItemId)) continue;
                meals.Add(option);
                if (Fill(typeIndex + 1)) return true;
                meals.RemoveAt(meals.Count - 1);
                chosenDishes.Remove(option.MealItemId);
            }
            return Fill(typeIndex + 1);
        }
        if (!Fill(0)) meals = best;
        chosenDishes.Clear();
        chosenDishes.UnionWith(meals.Select(o => o.MealItemId));
        foreach (var option in Rank(eligible.Where(o => MealTypeClassification.IsSnack(o.Slot.MealType.Code))))
        {
            if (snacks.Count == snackCount) break;
            if (chosenDishes.Add(option.MealItemId)) snacks.Add(option);
        }
        var reasons = new List<string>();
        if (meals.Count < mealCount)
            reasons.Add($"This delivery day requires {mealCount} distinct eligible meal types, but only {meals.Count} could be assigned with distinct dishes. Our team needs to review the menu configuration.");
        if (snacks.Count < snackCount)
            reasons.Add($"This delivery day requires {snackCount} eligible snacks, but only {snacks.Count} could be assigned. Our team needs to review the menu configuration.");
        return new(meals, snacks, reasons.Count == 0 ? null : string.Join(" ", reasons));
    }
}