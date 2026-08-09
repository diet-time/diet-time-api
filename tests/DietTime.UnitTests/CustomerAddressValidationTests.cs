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

    private static UpsertCustomerAddressRequest ValidRequest(string addressType) => new(
        "Home", addressType, "A-126", "960", null, "91", "Al Wakrah",
        null, 25.1712345m, 51.6034567m, "Zone 91, Al Wakrah, Qatar", false);
}
