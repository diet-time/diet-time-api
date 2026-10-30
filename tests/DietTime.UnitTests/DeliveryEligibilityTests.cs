using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;

namespace DietTime.UnitTests;

public sealed class DeliveryEligibilityTests
{
    [Theory]
    [InlineData(true, FulfillmentMethods.HomeDelivery, true)]
    [InlineData(false, FulfillmentMethods.HomeDelivery, false)]
    [InlineData(true, FulfillmentMethods.Takeaway, true)]
    [InlineData(false, FulfillmentMethods.Takeaway, true)]
    [InlineData(true, "invalid", false)]
    [InlineData(false, "", false)]
    [InlineData(true, null, false)]
    public void Validates_package_eligibility(bool enabled, string? method, bool allowed) =>
        Assert.Equal(allowed, FulfillmentMethods.IsAllowed(enabled, method));

    [Fact]
    public void Takeaway_requires_neither_address_nor_delivery_slot()
    {
        var request = Request() with { FulfillmentMethod = FulfillmentMethods.Takeaway, TakeawayBranchId = Guid.NewGuid() };
        Assert.True(new PlaceOrderRequestValidator().Validate(request).IsValid);
    }

    [Fact]
    public void Takeaway_requires_a_branch_while_delivery_does_not()
    {
        var validator = new PlaceOrderRequestValidator();
        Assert.Contains(validator.Validate(Request() with { FulfillmentMethod = FulfillmentMethods.Takeaway }).Errors,
            x => x.PropertyName == "TakeawayBranchId");
        Assert.DoesNotContain(validator.Validate(Request()).Errors, x => x.PropertyName == "TakeawayBranchId");
    }

    [Fact]
    public void Delivery_requires_address_and_slot_and_unknown_methods_are_rejected()
    {
        var validator = new PlaceOrderRequestValidator();
        var result = validator.Validate(Request());
        Assert.Contains(result.Errors, x => x.PropertyName == "CustomerAddressId");
        Assert.Contains(result.Errors, x => x.PropertyName == "DeliveryTimeSlotId");
        Assert.Contains(validator.Validate(Request() with { FulfillmentMethod = "OTHER" }).Errors,
            x => x.PropertyName == "FulfillmentMethod");
    }

    [Fact]
    public void Missing_new_properties_preserve_delivery_defaults()
    {
        Assert.True(new MealPlanPricePackage().IsDeliveryEnabled);
        Assert.True(new UpsertMealPlanPricePackageRequest("TEST", "Test", "Test", 9, 0, true).IsDeliveryEnabled);
        Assert.Equal(FulfillmentMethods.HomeDelivery, new Order().FulfillmentMethod);
        Assert.Equal(OrderStatuses.PendingApproval, new Order().Status);
        Assert.Equal(FulfillmentMethods.HomeDelivery, Request().FulfillmentMethod);
    }

    private static PlaceOrderRequest Request() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        null, null, new DateOnly(2026, 11, 1), [1, 2, 3, 4, 6, 7]);
}
