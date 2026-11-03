using DietTime.Application;
using DietTime.Domain;

namespace DietTime.UnitTests;

public sealed class PreparationLocationTests
{
    [Fact]
    public void Snapshot_keeps_all_saved_fields_and_coordinates_instead_of_newer_linked_address()
    {
        var customer = Guid.NewGuid(); var addressId = Guid.NewGuid();
        var order = new Order { CustomerProfileId = customer, CustomerAddressId = addressId,
            DeliveryFormattedAddress = "Pinned location, Qatar", DeliveryBuildingNo = "25", DeliveryStreetNo = "10",
            DeliveryUnitNumber = "4B", DeliveryZoneNo = "45", DeliveryArea = "Al Sadd", DeliveryDirections = "Opposite the park",
            DeliveryLatitude = 25.28m, DeliveryLongitude = 51.53m };
        var current = new CustomerAddress { Id = addressId, CustomerProfileId = customer, FormattedAddress = "New default address", Latitude = 1m };
        var location = PreparationOrderLocation.Resolve(order, current);
        foreach (var part in new[] { "Building: 25", "Street: 10", "Unit: 4B", "Zone: 45", "Area: Al Sadd", "Opposite the park" })
            Assert.Contains(part, location.Address);
        Assert.DoesNotContain("Pinned location", location.Address);
        Assert.DoesNotContain("New default", location.Address);
        Assert.Equal(25.28m, location.Latitude); Assert.Equal(51.53m, location.Longitude);
    }

    [Theory]
    [InlineData("Pinned location, Qatar")]
    [InlineData("Qatar")]
    [InlineData("")]
    public void Generic_or_empty_snapshot_requires_verification_without_using_current_default(string text)
    {
        var order = new Order { DeliveryFormattedAddress = text, DeliveryLatitude = 25m, DeliveryLongitude = 51m };
        Assert.Null(PreparationOrderLocation.Resolve(order, new CustomerAddress { FormattedAddress = "New default" }).Address);
    }

    [Fact]
    public void Only_explicitly_linked_owned_address_is_used_when_snapshot_is_absent()
    {
        var address = new CustomerAddress { Id = Guid.NewGuid(), CustomerProfileId = Guid.NewGuid(),
            FormattedAddress = "Historical linked address", BuildingNo = "40", Latitude = 25m, Longitude = 51m };
        var order = new Order { CustomerProfileId = address.CustomerProfileId, CustomerAddressId = address.Id };
        Assert.Contains("Historical linked address", PreparationOrderLocation.Resolve(order, address).Address);
        address.CustomerProfileId = Guid.NewGuid();
        Assert.Null(PreparationOrderLocation.Resolve(order, address).Address);
        address.CustomerProfileId = order.CustomerProfileId; address.Id = Guid.NewGuid();
        Assert.Null(PreparationOrderLocation.Resolve(order, address).Address);
    }

    [Fact]
    public void Partial_location_keeps_known_zone_and_landmark_but_requires_address_verification()
    {
        var location = PreparationOrderLocation.Resolve(new Order { DeliveryFormattedAddress = "Pinned location, Doha, Qatar",
            DeliveryZoneNo = "45", DeliveryArea = "Al Sadd", DeliveryDirections = "Behind the park" });
        Assert.True(location.RequiresVerification);
        Assert.Contains("Zone: 45", location.Address);
        Assert.Contains("Area: Al Sadd", location.Address);
        Assert.Contains("Behind the park", location.Address);
        Assert.DoesNotContain("Pinned location", location.Address);
    }
}
