using System.Text.Json;
using DietTime.Contracts;
using DietTime.Domain;

namespace DietTime.UnitTests;

public sealed class PackagePriceTests
{
    [Theory]
    [InlineData(999, 799, 200, 20.02)]
    [InlineData(100, 0, 100, 100)]
    [InlineData(10.01, 9.99, 0.02, 0.20)]
    [InlineData(3, 2, 1, 33.33)]
    public void Computes_exact_decimal_savings(decimal regular, decimal discounted, decimal saving, decimal percentage)
    {
        var value = PackagePriceRules.Calculate(regular, discounted);
        Assert.Equal(discounted, value.EffectivePrice);
        Assert.Equal(saving, value.DiscountAmount);
        Assert.Equal(percentage, value.DiscountPercentage);
    }

    [Fact]
    public void No_discount_keeps_regular_price_and_zero_savings()
    {
        var value = PackagePriceRules.Calculate(999, null);
        Assert.Equal(999, value.EffectivePrice); Assert.Equal(0, value.DiscountAmount); Assert.Equal(0, value.DiscountPercentage);
        var response = new MealPlanPricingResponse(Guid.NewGuid(), Guid.NewGuid(), "Balance", "WEEK", "Week", Guid.NewGuid(), "Package", 2, 1, 999, "QAR", true);
        Assert.False(response.HasDiscount); Assert.Equal(999, response.RegularPrice); Assert.Equal(999, response.EffectivePrice);
    }

    [Theory]
    [InlineData(100, -1)] [InlineData(100, 100)] [InlineData(100, 101)]
    [InlineData(100, 0.001)] [InlineData(100.001, 99)] [InlineData(-1, 0)]
    public void Rejects_invalid_discount_and_precision(decimal regular, decimal discounted) =>
        Assert.False(PackagePriceRules.IsValid(regular, discounted));

    [Fact]
    public void Omitted_discount_preserves_legacy_update_semantics_and_explicit_null_clears()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var omitted = JsonSerializer.Deserialize<UpsertMealPlanPricingRequest>("{\"price\":999}", options)!;
        var cleared = JsonSerializer.Deserialize<UpsertMealPlanPricingRequest>("{\"price\":999,\"discountedPrice\":null}", options)!;
        var set = JsonSerializer.Deserialize<UpsertMealPlanPricingRequest>("{\"price\":999,\"discountedPrice\":799}", options)!;
        Assert.False(omitted.DiscountedPriceSpecified); Assert.True(cleared.DiscountedPriceSpecified);
        Assert.Null(cleared.DiscountedPrice); Assert.Equal(799, set.DiscountedPrice);
    }
}
