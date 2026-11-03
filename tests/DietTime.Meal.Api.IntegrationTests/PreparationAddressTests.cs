using DietTime.Application;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.Extensions.Options;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Preparation_uses_snapshot_components_and_coordinates_and_only_linked_address_for_legacy_orders()
    {
        var menu = await PrepareAsync();
        var linked = new CustomerAddress { Id = Guid.NewGuid(), CustomerProfileId = order.CustomerProfileId,
            FormattedAddress = "New customer default address", BuildingNo = "999", Area = "Current area",
            Latitude = 1m, Longitude = 2m, IsDefault = true, CreatedAt = clock.Now, UpdatedAt = clock.Now };
        db.CustomerAddresses.Add(linked);
        order.CustomerAddressId = linked.Id;
        order.DeliveryFormattedAddress = "Pinned location, Qatar";
        order.DeliveryBuildingNo = "25"; order.DeliveryStreetNo = "10"; order.DeliveryZoneNo = "45";
        order.DeliveryArea = "Al Sadd"; order.DeliveryDirections = "Behind the community park";
        order.DeliveryLatitude = 25.28m; order.DeliveryLongitude = 51.53m;
        await db.SaveChangesAsync();
        var calendar = new AdminDeliveryCalendarService(db, Options.Create(new MenuRotationOptions()), clock);
        var summary = await calendar.GetPreparationSummaryAsync(menu.DeliveryDate, default);
        var selection = Assert.Single(summary.Selections);
        Assert.Contains("Building: 25", selection.DeliveryAddress);
        Assert.Contains("Street: 10", selection.DeliveryAddress);
        Assert.Contains("Zone: 45", selection.DeliveryAddress);
        Assert.Contains("Behind the community park", selection.DeliveryAddress);
        Assert.DoesNotContain("New customer", selection.DeliveryAddress);
        Assert.DoesNotContain("Pinned location", selection.DeliveryAddress);
        Assert.Equal(25.28m, selection.DeliveryLatitude); Assert.Equal(51.53m, selection.DeliveryLongitude);
        Assert.Equal(1, summary.DeliveryOrderCount); Assert.Equal(0, summary.TakeawayOrderCount);

        order.DeliveryBuildingNo = null; order.DeliveryStreetNo = null; order.DeliveryZoneNo = null;
        order.DeliveryArea = ""; order.DeliveryDirections = null;
        await db.SaveChangesAsync();
        Assert.Null(Assert.Single((await calendar.GetPreparationSummaryAsync(menu.DeliveryDate, default)).Selections).DeliveryAddress);

        order.DeliveryFormattedAddress = null; order.DeliveryLatitude = null; order.DeliveryLongitude = null;
        order.DeliveryAddressName = null; order.DeliveryAddressType = ""; order.DeliveryUnitNumber = null;
        await db.SaveChangesAsync();
        var legacy = Assert.Single((await calendar.GetPreparationSummaryAsync(menu.DeliveryDate, default)).Selections);
        Assert.Contains("New customer default address", legacy.DeliveryAddress);
        Assert.Equal(1m, legacy.DeliveryLatitude);

        order.CustomerAddressId = null;
        await db.SaveChangesAsync();
        Assert.Null(Assert.Single((await calendar.GetPreparationSummaryAsync(menu.DeliveryDate, default)).Selections).DeliveryAddress);

        order.FulfillmentMethod = "TAKEAWAY";
        var branch = new TakeawayBranch { Id = Guid.NewGuid(), Name = "Current branch name", Address = "Current branch address",
            CreatedAt = clock.Now, UpdatedAt = clock.Now };
        db.TakeawayBranches.Add(branch);
        order.TakeawayBranchId = branch.Id;
        order.TakeawayBranchName = "Saved branch"; order.TakeawayBranchAddress = "Saved branch address";
        await db.SaveChangesAsync();
        var takeaway = await calendar.GetPreparationSummaryAsync(menu.DeliveryDate, default);
        Assert.Equal(0, takeaway.DeliveryOrderCount); Assert.Equal(1, takeaway.TakeawayOrderCount);
        var pickup = Assert.Single(takeaway.Selections);
        Assert.Equal("Saved branch address", pickup.TakeawayBranchAddress);
        Assert.Null(pickup.DeliveryAddress); Assert.Null(pickup.DeliveryLatitude);

        order.TakeawayBranchName = null; order.TakeawayBranchAddress = null;
        await db.SaveChangesAsync();
        var legacyPickup = Assert.Single((await calendar.GetPreparationSummaryAsync(menu.DeliveryDate, default)).Selections);
        Assert.Equal("Current branch name", legacyPickup.TakeawayBranchName);
        Assert.Equal("Current branch address", legacyPickup.TakeawayBranchAddress);
    }
}
