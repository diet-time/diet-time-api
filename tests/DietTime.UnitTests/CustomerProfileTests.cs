using DietTime.Application;
using DietTime.Contracts;

namespace DietTime.UnitTests;

public sealed class CustomerProfileTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
    private readonly UpsertCustomerProfileRequestValidator validator =
        new(new FixedTimeProvider(Now));

    [Fact]
    public void Partial_onboarding_is_valid()
    {
        var result = validator.Validate(new UpsertCustomerProfileRequest
        {
            GenderCode = "MALE",
            PreferredLanguage = "en",
            OnboardingStatus = "IN_PROGRESS"
        });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Completed_onboarding_requires_all_mandatory_fields()
    {
        var result = validator.Validate(new UpsertCustomerProfileRequest
        {
            PreferredLanguage = "en",
            OnboardingStatus = "COMPLETED"
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.PropertyName == nameof(UpsertCustomerProfileRequest.GenderCode));
        Assert.Contains(result.Errors, x => x.PropertyName == nameof(UpsertCustomerProfileRequest.DateOfBirth));
        Assert.Contains(result.Errors, x => x.PropertyName == nameof(UpsertCustomerProfileRequest.HeightCm));
        Assert.Contains(result.Errors, x => x.PropertyName == nameof(UpsertCustomerProfileRequest.WeightKg));
        Assert.Contains(result.Errors, x => x.PropertyName == nameof(UpsertCustomerProfileRequest.GoalCode));
        Assert.Contains(result.Errors, x => x.PropertyName == nameof(UpsertCustomerProfileRequest.DailyRoutineCode));
        Assert.Contains(result.Errors, x => x.PropertyName == nameof(UpsertCustomerProfileRequest.ActivityLevelCode));
    }

    [Fact]
    public void Validation_rejects_invalid_ranges_dates_and_duplicates()
    {
        var duplicateAllergen = Guid.NewGuid();
        var result = validator.Validate(new UpsertCustomerProfileRequest
        {
            HeightCm = 49,
            WeightKg = 501,
            DateOfBirth = new DateOnly(2026, 7, 31),
            PreferredLanguage = "en",
            OnboardingStatus = "IN_PROGRESS",
            Preferences =
            [
                new("HIGH_PROTEIN", null, 5),
                new("high_protein", null, 4)
            ],
            Allergens =
            [
                new(duplicateAllergen, null, false, null),
                new(duplicateAllergen, null, false, null)
            ]
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.PropertyName == nameof(UpsertCustomerProfileRequest.HeightCm));
        Assert.Contains(result.Errors, x => x.PropertyName == nameof(UpsertCustomerProfileRequest.WeightKg));
        Assert.Contains(result.Errors, x => x.PropertyName == nameof(UpsertCustomerProfileRequest.DateOfBirth));
        Assert.Contains(result.Errors, x => x.PropertyName == nameof(UpsertCustomerProfileRequest.Preferences));
        Assert.Contains(result.Errors, x => x.PropertyName == nameof(UpsertCustomerProfileRequest.Allergens));
    }

    [Theory]
    [InlineData(18.49, "UNDERWEIGHT")]
    [InlineData(18.50, "NORMAL")]
    [InlineData(24.99, "NORMAL")]
    [InlineData(25.00, "OVERWEIGHT")]
    [InlineData(29.99, "OVERWEIGHT")]
    [InlineData(30.00, "OBESE")]
    public void Bmi_category_boundaries_are_correct(double requestedBmi, string expectedCategory)
    {
        const decimal heightCm = 100m;
        var result = CustomerProfileCalculations.Bmi(heightCm, (decimal)requestedBmi);

        Assert.Equal((decimal)requestedBmi, result.Bmi);
        Assert.Equal(expectedCategory, result.Category);
    }

    [Fact]
    public void Bmi_is_rounded_and_is_absent_when_an_input_is_missing()
    {
        var calculated = CustomerProfileCalculations.Bmi(175m, 82m);

        Assert.Equal(26.78m, calculated.Bmi);
        Assert.Equal("OVERWEIGHT", calculated.Category);
        Assert.Equal((null, null), CustomerProfileCalculations.Bmi(null, 82m));
    }

    [Fact]
    public void Nutrition_uses_mifflin_st_jeor_and_configured_defaults()
    {
        var calculator = new CustomerNutritionCalculator(new CustomerNutritionOptions());

        var result = calculator.Calculate(new(
            "MALE",
            new DateOnly(1990, 6, 15),
            175,
            82,
            "LOSE_WEIGHT",
            "LIGHT_ACTIVITY",
            new DateOnly(2026, 7, 30)));

        Assert.NotNull(result);
        Assert.Equal(1891, result.DailyCaloriesKcal);
        Assert.Equal(131.2m, result.DailyProteinG);
        Assert.Equal(199.8m, result.DailyCarbohydratesG);
        Assert.Equal(63.0m, result.DailyFatG);
        Assert.Equal(26.5m, result.DailyFiberG);
        Assert.Equal(2870, result.DailyWaterMl);
        Assert.Equal("MIFFLIN_ST_JEOR", result.CalculationMethod);
    }

    [Theory]
    [InlineData(null, "LIGHT_ACTIVITY")]
    [InlineData("OTHER", "LIGHT_ACTIVITY")]
    [InlineData("MALE", null)]
    [InlineData("MALE", "UNKNOWN")]
    public void Nutrition_is_skipped_for_missing_or_unsupported_inputs(
        string? gender,
        string? activityLevel)
    {
        var calculator = new CustomerNutritionCalculator(new CustomerNutritionOptions());

        var result = calculator.Calculate(new(
            gender,
            new DateOnly(1990, 6, 15),
            175,
            82,
            "MAINTAIN_WEIGHT",
            activityLevel,
            new DateOnly(2026, 7, 30)));

        Assert.Null(result);
    }

    [Fact]
    public void Preferred_name_is_required_and_limited_to_100_characters()
    {
        var validator = new UpdateCustomerPreferredNameRequestValidator();

        Assert.False(validator.Validate(new UpdateCustomerPreferredNameRequest(" ")).IsValid);
        Assert.False(validator.Validate(new UpdateCustomerPreferredNameRequest(new string('x', 101))).IsValid);
        Assert.True(validator.Validate(new UpdateCustomerPreferredNameRequest("Noor")).IsValid);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
