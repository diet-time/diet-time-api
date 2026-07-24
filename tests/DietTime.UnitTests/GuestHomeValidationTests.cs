using DietTime.Application;
using DietTime.Contracts;

namespace DietTime.UnitTests;

public sealed class GuestHomeValidationTests
{
    private readonly GuestHomeQueryValidator validator = new();

    [Fact]
    public void Accepts_supported_defaults()
    {
        var result = validator.Validate(new GuestHomeQuery());

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("fr", "ALL", 1, 20)]
    [InlineData("en", "BRUNCH", 1, 20)]
    [InlineData("en", "ALL", 0, 20)]
    [InlineData("en", "ALL", 1, 101)]
    public void Rejects_invalid_query_values(string language, string mealTime, int page, int pageSize)
    {
        var result = validator.Validate(new GuestHomeQuery(language, null, null, mealTime, page, pageSize));

        Assert.False(result.IsValid);
    }
}
