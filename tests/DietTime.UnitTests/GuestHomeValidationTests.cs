using DietTime.Application;
using DietTime.Contracts;

namespace DietTime.UnitTests;

public sealed class GuestHomeValidationTests
{
    private readonly GuestHomeQueryValidator validator = new();
    private readonly GuestMenuQueryValidator menuValidator = new();
    private readonly GuestAllergensQueryValidator allergensValidator = new();

    [Fact]
    public void Accepts_supported_defaults()
    {
        var result = validator.Validate(new GuestHomeQuery());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Rejects_unsupported_home_language()
    {
        var result = validator.Validate(new GuestHomeQuery("fr"));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Menu_requires_supported_language_and_date()
    {
        Assert.False(menuValidator.Validate(new GuestMenuQuery(new DateOnly(2026, 7, 23), "fr")).IsValid);
        Assert.False(menuValidator.Validate(new GuestMenuQuery(default)).IsValid);
        Assert.True(menuValidator.Validate(new GuestMenuQuery(new DateOnly(2026, 7, 23))).IsValid);
    }

    [Fact]
    public void Allergens_require_a_supported_language()
    {
        Assert.True(allergensValidator.Validate(new GuestAllergensQuery()).IsValid);
        Assert.True(allergensValidator.Validate(new GuestAllergensQuery("ar")).IsValid);
        Assert.False(allergensValidator.Validate(new GuestAllergensQuery("fr")).IsValid);
    }
}
