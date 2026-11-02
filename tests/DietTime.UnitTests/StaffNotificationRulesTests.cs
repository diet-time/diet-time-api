using DietTime.Application;
namespace DietTime.UnitTests;
public sealed class StaffNotificationRulesTests
{
    [Theory]
    [InlineData(null,"PENDING_APPROVAL","ORDER_RECEIVED")]
    [InlineData(null,"PENDING","ORDER_RECEIVED")]
    [InlineData("PENDING_APPROVAL","CONFIRMED","ORDER_APPROVED")]
    [InlineData("PENDING","CONFIRMED","ORDER_APPROVED")]
    [InlineData("PENDING_APPROVAL","REJECTED","ORDER_REJECTED")]
    [InlineData("CONFIRMED","CANCELLED","ORDER_CANCELLED")]
    [InlineData("CONFIRMED","CONFIRMED",null)]
    [InlineData("CONFIRMED","ACTIVE",null)]
    [InlineData(null,"CONFIRMED",null)]
    [InlineData("ACTIVE","ACTIVE",null)]
    public void Only_real_order_events_are_selected(string? previous,string status,string? expected) => Assert.Equal(expected,StaffNotificationRules.OrderEvent(previous,status));
    [Fact]
    public void Role_event_matrix_is_explicit() {
        Assert.Equal(7,StaffNotificationRules.AdminEvents.Length);
    }
    [Fact]
    public void Every_supported_event_has_order_specific_copy() {
        foreach(var type in StaffNotificationRules.AdminEvents) {
            var content=StaffNotificationRules.Content(type,"ORD-123",new(2026,11,2));
            Assert.False(string.IsNullOrWhiteSpace(content.Title)); Assert.Contains("ORD-123",content.Message);
        }
    }
    [Fact]
    public void Customer_copy_uses_the_order_customer_name_and_keeps_a_safe_fallback() {
        var placed=StaffNotificationRules.Content("ORDER_RECEIVED","ORD-42",null,"  Sara Ali  ");
        Assert.Contains("Sara Ali placed order ORD-42",placed.Message);
        var confirmed=StaffNotificationRules.Content("ORDER_APPROVED","ORD-42",null,"Sara Ali");
        Assert.Contains("Sara Ali",confirmed.Message);Assert.Contains("confirmed",confirmed.Message);
        Assert.Contains("A customer placed order",StaffNotificationRules.Content("ORDER_RECEIVED","ORD-42",null).Message);
    }
    [Fact]
    public void Links_reuse_existing_screens() {
        var order=Guid.NewGuid();var day=Guid.NewGuid();
        Assert.Equal($"/orders?orderId={order}",StaffNotificationRules.Target(order,null,null));
        Assert.Equal($"/operations/delivery-calendar?date=2026-11-02&serviceDayId={day}",StaffNotificationRules.Target(order,day,new(2026,11,2)));
    }
}
