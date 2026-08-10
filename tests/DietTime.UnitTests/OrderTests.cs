using DietTime.Application;
using DietTime.Contracts;

namespace DietTime.UnitTests;

public sealed class OrderTests
{
    [Theory]
    [InlineData("SNACK")]
    [InlineData("snack")]
    [InlineData("SNACK_DESSERT")]
    [InlineData("snack_dessert")]
    public void Recognizes_catalogue_snack_codes(string code)
    {
        Assert.True(MealTypeClassification.IsSnack(code));
    }

    [Theory]
    [InlineData("BREAKFAST")]
    [InlineData("LUNCH")]
    [InlineData("DINNER")]
    public void Does_not_classify_regular_meals_as_snacks(string code)
    {
        Assert.False(MealTypeClassification.IsSnack(code));
    }

    [Fact]
    public void Calculates_end_date_using_service_days_only()
    {
        var start = new DateOnly(2026, 8, 11); // Tuesday

        var end = OrderSchedulingRules.CalculateEndDate(start, [2, 3, 4, 5, 6], 20);

        Assert.Equal(new DateOnly(2026, 9, 5), end);
    }

    [Fact]
    public void Place_order_validator_rejects_duplicates_and_invalid_quantities()
    {
        var mealTypeId = Guid.NewGuid();
        var request = new PlaceOrderRequest(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new DateOnly(2026, 8, 11), [2, 2],
            [new(mealTypeId, 1), new(mealTypeId, 0)]);

        var result = new PlaceOrderRequestValidator().Validate(request);

        Assert.Contains(result.Errors, error => error.PropertyName == nameof(request.DeliveryDays));
        Assert.Contains(result.Errors, error => error.PropertyName == nameof(request.Meals));
        Assert.Contains(result.Errors, error => error.PropertyName.EndsWith("Quantity", StringComparison.Ordinal));
    }
}
