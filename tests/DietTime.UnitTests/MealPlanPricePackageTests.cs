using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using FluentValidation;

namespace DietTime.UnitTests;

public sealed class MealPlanPricePackageTests
{
    private readonly UpsertMealPlanPricePackageRequestValidator validator = new();

    [Fact]
    public void Valid_package_supports_Arabic_and_data_driven_codes()
    {
        var request = new UpsertMealPlanPricePackageRequest(
            "CORPORATE MONTH", "Corporate Month", "شهر الشركات", 30, 4, true);
        var result = validator.Validate(request);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Package_duration_must_be_positive(int durationDays)
    {
        var request = new UpsertMealPlanPricePackageRequest(
            "WEEK", "1 Week", "أسبوع واحد", durationDays, 1, true);
        var result = validator.Validate(request);

        Assert.Contains(result.Errors, x =>
            x.PropertyName == nameof(UpsertMealPlanPricePackageRequest.DurationDays));
    }

    [Theory]
    [InlineData("", "Arabic")]
    [InlineData("English", "")]
    [InlineData("   ", "Arabic")]
    [InlineData("English", "   ")]
    public void Package_names_are_required(string nameEn, string nameAr)
    {
        var request = new UpsertMealPlanPricePackageRequest("WEEK", nameEn, nameAr, 6, 1, true);
        var result = validator.Validate(request);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Pricing_contract_keeps_legacy_duration_and_adds_optional_package()
    {
        var legacy = new UpsertMealPlanPriceRequest(
            Guid.NewGuid(), 6, 3, 1, "QAR", 300m,
            DateTimeOffset.Parse("2026-08-01T00:00:00Z"), null, true);
        const string packageId = "WEEK";
        var packaged = legacy with { DurationDays = null, MealPlanPricePackageId = packageId };

        Assert.Equal(6, legacy.DurationDays);
        Assert.Null(legacy.MealPlanPricePackageId);
        Assert.Null(packaged.DurationDays);
        Assert.Equal(packageId, packaged.MealPlanPricePackageId);
    }

    [Fact]
    public void Package_entity_preserves_historical_price_navigation()
    {
        var package = new MealPlanPricePackage { Code = "WEEK", DurationDays = 6 };
        package.Prices.Add(new MealPlanPrice { DurationDays = 6, Package = package });

        Assert.Single(package.Prices);
        Assert.Equal(6, package.Prices.Single().DurationDays);
    }
}
