using DietTime.Application;
using DietTime.Domain;
using DietTime.Infrastructure;
namespace DietTime.UnitTests;

public sealed class WhatsAppTemplateRegistryTests
{
    [Theory]
    [InlineData("ORDER_RECEIVED", "STAFF", "diet_time_new_order_admin", 10)]
    [InlineData("ORDER_APPROVED", "CUSTOMER", "diet_time_order_confirmed", 7)]
    [InlineData("PAYMENT_RECEIVED", "CUSTOMER", "diet_time_payment_received", 5)]
    [InlineData("ORDER_CANCELLED", "CUSTOMER", "diet_time_order_cancelled", 5)]
    [InlineData("PACKAGE_PAUSED", "CUSTOMER", "diet_time_package_paused", 6)]
    [InlineData("PACKAGE_RESUMED", "CUSTOMER", "diet_time_package_resumed", 6)]
    [InlineData("MEAL_REMINDER", "CUSTOMER", "diet_time_meal_reminder", 3)]
    public void All_seven_templates_have_exact_ordered_variables(string code, string audience, string name, int count)
    {
        var sid = "HX" + new string('1',32);
        var order = new Order { OrderNumber = "ORD-123", PlanName = "Balance", PlanDurationName = "12 days", StartDate = new(2026,11,10),
            TotalAmount = 999, CurrencyCode = "QAR", FulfillmentMethod = "TAKEAWAY" };
        var message = new OrderNotification { EventCode = code, Audience = audience, Reason = "Customer travelling" };
        var context = new Dictionary<string,string> { ["eventDate"] = "05 Nov 2026", ["pauseStart"] = "11 Nov 2026",
            ["resumeDate"] = "20 Nov 2026", ["remainingDays"] = "10", ["endDate"] = "01 Dec 2026", ["serviceDate"] = "10 Nov 2026", ["deadline"] = "09 Nov 2026 19:00 Qatar" };
        var templates = new OrderWhatsAppTemplateOptions { ApprovedContentSid = sid, PaymentReceivedContentSid = sid, CancelledContentSid = sid,
            PausedContentSid = sid, ResumedContentSid = sid, MealReminderContentSid = sid };
        var result = WhatsAppTemplateRegistry.Build(message, order, "Ahmed", "+97455555555", "2 Meals + 1 Snacks", "Azizia, Doha", context, templates,
            new TwilioWhatsAppOptions { NewOrderContentSid = sid });
        var expected = code switch {
            "ORDER_RECEIVED" => new[] { "ORD-123", "Ahmed", "+97455555555", "Balance", "12 days", "2 Meals + 1 Snacks", "10 Nov 2026", "Takeaway", "Azizia, Doha", "QAR 999.00" },
            "ORDER_APPROVED" => new[] { "Ahmed", "ORD-123", "Balance", "12 days", "2 Meals + 1 Snacks", "10 Nov 2026", "Takeaway" },
            "PAYMENT_RECEIVED" => new[] { "Ahmed", "ORD-123", "Balance", "QAR 999.00", "05 Nov 2026" },
            "ORDER_CANCELLED" => new[] { "Ahmed", "ORD-123", "Balance", "05 Nov 2026", "Customer travelling" },
            "PACKAGE_PAUSED" => new[] { "Ahmed", "ORD-123", "Balance", "11 Nov 2026", "20 Nov 2026", "10" },
            "PACKAGE_RESUMED" => new[] { "Ahmed", "ORD-123", "Balance", "20 Nov 2026", "10", "01 Dec 2026" },
            _ => new[] { "Ahmed", "10 Nov 2026", "09 Nov 2026 19:00 Qatar" }
        };
        Assert.Equal(name,result.Name); Assert.Equal(sid,result.ContentSid); Assert.Equal(count,result.Variables.Count);
        Assert.Equal(expected, Enumerable.Range(1,count).Select(i => result.Variables[i.ToString()]));
    }
    [Fact]
    public void Location_uses_only_order_snapshot_includes_structured_fields_and_maps_and_avoids_generic_pin()
    {
        var order = new Order { DeliveryFormattedAddress = "Pinned location, Qatar", DeliveryBuildingNo = "10", DeliveryStreetNo = "20", DeliveryZoneNo = "30", DeliveryArea = "Doha",
            DeliveryDirections = "Near park", DeliveryLatitude = 25.2m, DeliveryLongitude = 51.5m };
        var location = WhatsAppTemplateRegistry.Location(order);
        Assert.Contains("Building 10",location); Assert.Contains("Street 20",location); Assert.Contains("Zone 30",location);
        Assert.Contains("Near park",location); Assert.Contains("https://www.google.com/maps?q=25.2,51.5",location); Assert.DoesNotContain("Pinned location",location);
        Assert.Equal("Address verification required",WhatsAppTemplateRegistry.Location(new Order()));
        Assert.Equal("Azizia, Branch address",WhatsAppTemplateRegistry.Location(new Order { FulfillmentMethod="TAKEAWAY",TakeawayBranchName="Azizia",TakeawayBranchAddress="Branch address" }));
    }
    [Fact]
    public void Repeated_and_out_of_order_status_updates_never_downgrade_delivery()
    {
        var message = new OrderNotification(); var now=DateTimeOffset.UtcNow;
        foreach(var state in new[]{"queued","read","delivered","sent","failed","read"}) WhatsAppDeliveryState.Apply(message,state,now);
        Assert.Equal("READ",message.Status); Assert.Equal("read",message.ProviderStatus); Assert.NotNull(message.DeliveredAt); Assert.NotNull(message.ReadAt);
        Assert.Null(message.LastError);
    }
    [Fact]
    public void Missing_required_template_values_fail_before_dispatch_and_reminders_default_off()
    {
        Assert.False(new OrderWhatsAppTemplateOptions().MealRemindersEnabled);
        Assert.Throws<InvalidOperationException>(()=>WhatsAppTemplateRegistry.Build(new OrderNotification { EventCode="MEAL_REMINDER" },new Order(),"Ahmed","", "","",new Dictionary<string,string>(),new(),new TwilioWhatsAppOptions()));
    }
}
