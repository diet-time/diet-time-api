using DietTime.Application;
using DietTime.Domain;
namespace DietTime.UnitTests;

public sealed class OrderWorkflowPolicyTests
{
    [Theory]
    [InlineData("PENDING_APPROVAL","CONFIRMED",true)]
    [InlineData("PENDING_APPROVAL","REJECTED",true)]
    [InlineData("PENDING_APPROVAL","ON_HOLD",false)]
    [InlineData("PENDING_APPROVAL","CANCELLED",true)]
    [InlineData("ON_HOLD","PENDING_APPROVAL",false)]
    [InlineData("ON_HOLD","CONFIRMED",false)]
    [InlineData("CONFIRMED","CONFIRMED",false)]
    [InlineData("REJECTED","CONFIRMED",false)]
    [InlineData("CANCELLED","CONFIRMED",false)]
    [InlineData("ACTIVE","COMPLETED",false)]
    [InlineData("CONFIRMED","ACTIVE",false)]
    [InlineData("LEGACY","ACTIVE",false)]
    public void Transitions_are_explicit(string from,string to,bool allowed) => Assert.Equal(allowed,OrderWorkflowPolicy.CanTransition(from,to));
    [Theory]
    [InlineData("PENDING_APPROVAL","PAID",false,false)]
    [InlineData("REJECTED","PAID",false,false)]
    [InlineData("ON_HOLD","PAID",false,false)]
    [InlineData("CONFIRMED","PENDING",false,true)]
    [InlineData("CONFIRMED","PENDING",true,false)]
    [InlineData("CONFIRMED","PAID",true,true)]
    [InlineData("ACTIVE","FAILED",false,false)]
    [InlineData("ACTIVE","REFUND_PENDING",false,false)]
    public void Fulfillment_requires_approval_and_payment_policy(string status,string payment,bool prepaid,bool allowed) =>
        Assert.Equal(allowed,OrderWorkflowPolicy.CanFulfill(new Order { Status=status,PaymentStatus=payment,RequiresPrepayment=prepaid }));
    [Theory]
    [InlineData("CONFIRMED", "PENDING", false, 0, 2, true)]
    [InlineData("CONFIRMED", "PENDING", true, 0, 2, false)]
    [InlineData("CONFIRMED", "PAID", true, -1, 0, true)]
    [InlineData("CONFIRMED", "FAILED", false, 0, 2, false)]
    [InlineData("CONFIRMED", "PAID", false, 1, 3, false)]
    [InlineData("CONFIRMED", "PAID", false, -3, -1, false)]
    [InlineData("PENDING_APPROVAL", "PAID", false, 0, 2, false)]
    [InlineData("COMPLETED", "PAID", false, 0, 2, false)]
    [InlineData("CANCELLED", "PAID", false, 0, 2, false)]
    [InlineData("REJECTED", "PAID", false, 0, 2, false)]
    [InlineData("ON_HOLD", "PAID", false, 0, 2, false)]
    public void Automatic_activation_requires_approved_current_payment_eligible_period(string status, string payment,
        bool prepaid, int start, int end, bool expected)
    {
        var today = new DateOnly(2026, 10, 31);
        var order = new Order { Status = status, PaymentStatus = payment, RequiresPrepayment = prepaid,
            StartDate = today.AddDays(start), EndDate = today.AddDays(end) };
        Assert.Equal(expected, OrderWorkflowPolicy.CanAutomaticallyActivate(order, today));
        order.FulfillmentBlockedReason = "Requires review";
        Assert.False(OrderWorkflowPolicy.CanAutomaticallyActivate(order, today));
    }
    [Fact]
    public void Repeated_payment_and_implicit_refunds_are_rejected()
    {
        Assert.False(OrderWorkflowPolicy.CanChangePayment("PAID","PAID"));
        Assert.False(OrderWorkflowPolicy.CanChangePayment("PAID","REFUNDED"));
        Assert.True(OrderWorkflowPolicy.CanChangePayment("PAID","REFUND_PENDING"));
        Assert.True(OrderWorkflowPolicy.CanChangePayment("REFUND_PENDING","REFUNDED"));
    }
}
