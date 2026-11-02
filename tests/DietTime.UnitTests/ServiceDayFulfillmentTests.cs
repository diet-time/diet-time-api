using DietTime.Application;
using DietTime.Domain;

namespace DietTime.UnitTests;

public sealed class ServiceDayFulfillmentTests
{
    [Theory]
    [InlineData("HOME_DELIVERY", "DELIVERED")]
    [InlineData("TAKEAWAY", "COLLECTED")]
    public void Future_service_day_is_rejected_without_modification(string method, string target)
    {
        var (order, day) = Scheduled(method);
        var error = Assert.Throws<FulfillmentException>(() => ServiceDayFulfillment.Apply(day, order, target, DateTimeOffset.Parse("2026-10-31T20:59:59Z"), Guid.NewGuid()));
        Assert.Equal(409, error.Status);
        Assert.Equal("service_day_in_future", error.Code);
        Assert.Equal("PLANNED", day.Status);
        Assert.Null(day.FulfilledAt);
        Assert.Null(day.FulfilledBy);
    }

    [Fact]
    public void Service_date_begins_at_Qatar_midnight()
    {
        var (order, day) = Scheduled();
        Assert.True(ServiceDayFulfillment.Apply(day, order, "DELIVERED", DateTimeOffset.Parse("2026-10-31T21:00:00Z"), null));
    }
    private static (Order Order, SubscriptionServiceDay Day) Scheduled(string method = "HOME_DELIVERY")
    {
        var order = new Order { Id = Guid.NewGuid(), Status = "CONFIRMED", PaymentStatus = "PENDING",
            FulfillmentMethod = method, StartDate = new(2026, 11, 1), EndDate = new(2026, 11, 30) };
        order.DeliveryDays.Add(new OrderDeliveryDay { DayOfWeek = 7 });
        return (order, new SubscriptionServiceDay { Id = Guid.NewGuid(), OrderId = order.Id, ServiceDate = new(2026, 11, 1), Status = "PLANNED" });
    }

    [Theory]
    [InlineData("HOME_DELIVERY", "DELIVERED")]
    [InlineData("TAKEAWAY", "COLLECTED")]
    public void Completion_is_independent_of_selection_and_order_status(string method, string status)
    {
        var (order, day) = Scheduled(method);
        var other = new SubscriptionServiceDay { OrderId = order.Id, ServiceDate = day.ServiceDate.AddDays(1) };
        var actor = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.FromHours(3));
        Assert.True(ServiceDayFulfillment.Apply(day, order, status, now, actor));
        Assert.Equal(status, day.Status);
        Assert.Equal(TimeSpan.Zero, day.FulfilledAt!.Value.Offset);
        Assert.Equal(now.ToUniversalTime(), day.FulfilledAt);
        Assert.Equal(actor, day.FulfilledBy);
        Assert.Equal("CONFIRMED", order.Status);
        Assert.Equal("PENDING", order.PaymentStatus);
        Assert.Equal("PLANNED", other.Status);
        Assert.Null(other.FulfilledAt);
        Assert.False(day.SelectionLocked);
        Assert.Null(day.AutoFinalizedAt);
        Assert.Empty(day.Selections);
        Assert.False(ServiceDayFulfillment.Apply(day, order, status, now.AddHours(1), Guid.NewGuid()));
        Assert.Equal(now.ToUniversalTime(), day.FulfilledAt);
        Assert.Equal(actor, day.FulfilledBy);
        Assert.Equal(2, day.Revision);
        var conflict = Assert.Throws<FulfillmentException>(() => ServiceDayFulfillment.Apply(day, order,
            status == "DELIVERED" ? "COLLECTED" : "DELIVERED", now, actor));
        Assert.Equal(409, conflict.Status);
    }

    [Theory]
    [InlineData("HOME_DELIVERY", "COLLECTED")]
    [InlineData("TAKEAWAY", "DELIVERED")]
    [InlineData("HOME_DELIVERY", "FINALIZED")]
    [InlineData("HOME_DELIVERY", "COMPLETED")]
    [InlineData("HOME_DELIVERY", "READY")]
    public void Invalid_target_is_rejected(string method, string target)
    {
        var (order, day) = Scheduled(method);
        Assert.Equal(400, Assert.Throws<FulfillmentException>(() => ServiceDayFulfillment.Apply(day, order, target, DateTimeOffset.UtcNow, null)).Status);
        Assert.Equal("PLANNED", day.Status);
    }

    [Theory]
    [InlineData("PENDING_APPROVAL", "PENDING", false)]
    [InlineData("CANCELLED", "PAID", false)]
    [InlineData("CONFIRMED", "FAILED", false)]
    [InlineData("CONFIRMED", "PENDING", true)]
    public void Ineligible_order_is_rejected(string status, string payment, bool prepayment)
    {
        var (order, day) = Scheduled();
        order.Status = status; order.PaymentStatus = payment; order.RequiresPrepayment = prepayment;
        Assert.Equal(409, Assert.Throws<FulfillmentException>(() => ServiceDayFulfillment.Apply(day, order, "DELIVERED", DateTimeOffset.UtcNow, null)).Status);
        Assert.Null(day.FulfilledAt);
    }

    [Theory]
    [InlineData("SKIPPED")]
    [InlineData("CANCELLED")]
    [InlineData("PREPARING")]
    public void Conflicting_source_is_rejected(string status)
    {
        var (order, day) = Scheduled(); day.Status = status;
        Assert.Equal(409, Assert.Throws<FulfillmentException>(() => ServiceDayFulfillment.Apply(day, order, "DELIVERED", DateTimeOffset.UtcNow, null)).Status);
    }
}
