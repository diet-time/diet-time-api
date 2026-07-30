namespace DietTime.Application;

public sealed class CustomerNutritionOptions
{
    public const string SectionName = "CustomerNutrition";
    public int MinimumDailyCaloriesKcal { get; set; } = 1200;
    public decimal ProteinGramsPerKg { get; set; } = 1.6m;
    public decimal FatCaloriesRatio { get; set; } = 0.30m;
    public decimal FiberGramsPerThousandCalories { get; set; } = 14m;
    public decimal WaterMlPerKg { get; set; } = 35m;
    public Dictionary<string, decimal> ActivityMultipliers { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MOSTLY_SITTING"] = 1.20m,
        ["LIGHT_ACTIVITY"] = 1.375m,
        ["ACTIVE_LIFESTYLE"] = 1.55m,
        ["VERY_ACTIVE"] = 1.725m,
        ["ATHLETE"] = 1.90m
    };
    public Dictionary<string, int> GoalAdjustmentsKcal { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LOSE_WEIGHT"] = -500,
        ["MAINTAIN_WEIGHT"] = 0,
        ["GAIN_WEIGHT"] = 300,
        ["BUILD_MUSCLE"] = 250,
        ["EAT_HEALTHIER"] = 0
    };
}

public sealed class CustomerNutritionCalculator(CustomerNutritionOptions options)
    : ICustomerNutritionCalculator
{
    public CustomerNutritionCalculationResult? Calculate(CustomerNutritionCalculationInput input)
    {
        if (input.DateOfBirth is null ||
            input.HeightCm is null ||
            input.WeightKg is null ||
            string.IsNullOrWhiteSpace(input.GenderCode) ||
            string.IsNullOrWhiteSpace(input.ActivityLevelCode) ||
            !options.ActivityMultipliers.TryGetValue(input.ActivityLevelCode, out var activityMultiplier))
        {
            return null;
        }

        var genderAdjustment = input.GenderCode.Trim().ToUpperInvariant() switch
        {
            "MALE" => 5m,
            "FEMALE" => -161m,
            _ => (decimal?)null
        };
        if (genderAdjustment is null)
            return null;

        var age = CustomerProfileCalculations.Age(input.DateOfBirth.Value, input.CalculationDate);
        if (age < 0)
            return null;

        var bmr =
            10m * input.WeightKg.Value +
            6.25m * input.HeightCm.Value -
            5m * age +
            genderAdjustment.Value;
        var goalAdjustment = !string.IsNullOrWhiteSpace(input.GoalCode) &&
            options.GoalAdjustmentsKcal.TryGetValue(input.GoalCode, out var configuredAdjustment)
                ? configuredAdjustment
                : 0;
        var calories = Math.Max(
            options.MinimumDailyCaloriesKcal,
            (int)Math.Round(bmr * activityMultiplier + goalAdjustment, 0, MidpointRounding.AwayFromZero));

        var protein = Round(input.WeightKg.Value * options.ProteinGramsPerKg);
        var fat = Round(calories * options.FatCaloriesRatio / 9m);
        var carbohydrateCalories = Math.Max(0m, calories - protein * 4m - fat * 9m);
        var carbohydrates = Round(carbohydrateCalories / 4m);
        var fiber = Round(calories / 1000m * options.FiberGramsPerThousandCalories);
        var water = (int)Math.Round(input.WeightKg.Value * options.WaterMlPerKg, 0, MidpointRounding.AwayFromZero);

        return new(
            calories,
            protein,
            carbohydrates,
            fat,
            fiber,
            water,
            "MIFFLIN_ST_JEOR",
            "1.0");
    }

    private static decimal Round(decimal value) =>
        Math.Round(value, 1, MidpointRounding.AwayFromZero);
}

public static class CustomerProfileCalculations
{
    public static int Age(DateOnly dateOfBirth, DateOnly onDate)
    {
        var age = onDate.Year - dateOfBirth.Year;
        if (dateOfBirth > onDate.AddYears(-age))
            age--;
        return age;
    }

    public static (decimal? Bmi, string? Category) Bmi(decimal? heightCm, decimal? weightKg)
    {
        if (heightCm is null || weightKg is null)
            return (null, null);

        var heightMeters = heightCm.Value / 100m;
        var bmi = Math.Round(
            weightKg.Value / (heightMeters * heightMeters),
            2,
            MidpointRounding.AwayFromZero);
        var category = bmi switch
        {
            < 18.5m => "UNDERWEIGHT",
            < 25m => "NORMAL",
            < 30m => "OVERWEIGHT",
            _ => "OBESE"
        };
        return (bmi, category);
    }
}
