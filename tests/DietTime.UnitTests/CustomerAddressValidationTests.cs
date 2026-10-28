using DietTime.Application;
using DietTime.Contracts;

namespace DietTime.UnitTests;

public sealed class CustomerAddressValidationTests
{
    private readonly UpsertCustomerAddressRequestValidator validator = new();

    [Theory]
    [InlineData("HOME")]
    [InlineData("APARTMENT")]
    [InlineData("OFFICE")]
    [InlineData("OTHER")]
    public void Accepts_supported_address_types(string addressType)
    {
        var result = validator.Validate(ValidRequest(addressType));
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Rejects_invalid_type_missing_area_and_out_of_range_coordinates()
    {
        var result = validator.Validate(new UpsertCustomerAddressRequest(
            null, "VILLA", null, null, null, null, "", null, 90.0000001m, -180.0000001m, null));

        Assert.Contains(result.Errors, x => x.PropertyName == "AddressType");
        Assert.Contains(result.Errors, x => x.PropertyName == "Area");
        Assert.Contains(result.Errors, x => x.PropertyName == "Latitude");
        Assert.Contains(result.Errors, x => x.PropertyName == "Longitude");
    }

    [Theory]
    [InlineData("Al Wakrah")]
    [InlineData(" الوكرة ")]
    [InlineData("أمّ صلال")]
    [InlineData("Al-Dafna (West)")]
    public void Accepts_English_and_Arabic_area_names(string area)
    {
        Assert.True(validator.Validate(ValidRequest("HOME") with { Area = area }).IsValid);
    }

    [Theory]
    [InlineData("Al Wakrah2")]
    [InlineData("الوكرة٢")]
    [InlineData("---")]
    [InlineData("   ")]
    public void Rejects_digits_or_missing_letters_in_area(string area)
    {
        Assert.Contains(validator.Validate(ValidRequest("HOME") with { Area = area }).Errors, e => e.PropertyName == "Area");
    }

    [Theory]
    [InlineData("126")]
    [InlineData("A-126/B")]
    [InlineData("مبنى-١٢٦")]
    public void Accepts_identifiers_numeric_streets_and_optional_units(string building)
    {
        Assert.True(validator.Validate(ValidRequest("HOME") with {
            BuildingNo = building, StreetNo = "960", ZoneNo = "٩١", UnitNumber = "B/12", Directions = "Gate 3: call +974 12345678!"
        }).IsValid);
    }

    [Fact]
    public void Rejects_missing_building_street_and_non_numeric_zone()
    {
        var result = validator.Validate(ValidRequest("HOME") with { BuildingNo = " ", StreetNo = "", ZoneNo = "A91" });
        Assert.Contains(result.Errors, e => e.PropertyName == "BuildingNo");
        Assert.Contains(result.Errors, e => e.PropertyName == "StreetNo");
        Assert.Contains(result.Errors, e => e.PropertyName == "ZoneNo");
    }


    private static UpsertCustomerAddressRequest ValidRequest(string addressType) => new(
        "Home", addressType, "A-126", "960", null, "91", "Al Wakrah",
        null, 25.1712345m, 51.6034567m, "Zone 91, Al Wakrah, Qatar", false);
}
