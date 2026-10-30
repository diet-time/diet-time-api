using DietTime.Application;
using DietTime.Domain;
namespace DietTime.UnitTests;

public sealed class OrderWorkflowPolicyTests
{
    [Theory]
    [InlineData("PENDING_APPROVAL","CONFIRMED",true)]
    [InlineData("PENDING_APPROVAL","REJECTED",true)]
    [InlineData("PENDING_APPROVAL","ON_HOLD",true)]
    [InlineData("PENDING_APPROVAL","CANCELLED",true)]
    [InlineData("ON_HOLD","PENDING_APPROVAL",true)]
    [InlineData("ON_HOLD","CONFIRMED",false)]
    [InlineData("CONFIRMED","CONFIRMED",false)]
    [InlineData("REJECTED","CONFIRMED",false)]
    [InlineData("CANCELLED","CONFIRMED",false)]
    [InlineData("ACTIVE","COMPLETED",true)]
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
    [Fact]
    public void Repeated_payment_and_implicit_refunds_are_rejected()
    {
        Assert.False(OrderWorkflowPolicy.CanChangePayment("PAID","PAID"));
        Assert.False(OrderWorkflowPolicy.CanChangePayment("PAID","REFUNDED"));
        Assert.True(OrderWorkflowPolicy.CanChangePayment("PAID","REFUND_PENDING"));
        Assert.True(OrderWorkflowPolicy.CanChangePayment("REFUND_PENDING","REFUNDED"));
    }
}
