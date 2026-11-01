using DietTime.Domain;

namespace DietTime.Application;

public sealed class FulfillmentException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public static class ServiceDayFulfillment
{
    // PLANNED and CONFIRMED are existing storage codes for scheduled service days.
    public static string DisplayStatus(string status) => status is "PLANNED" or "CONFIRMED" ? "SCHEDULED" : status;

    public static bool Apply(SubscriptionServiceDay day, Order order, string? status, DateTimeOffset now, Guid? actor)
    {
        if (status is not ("DELIVERED" or "COLLECTED"))
            throw new FulfillmentException(400, "invalid_fulfillment_status", "Use DELIVERED for home delivery or COLLECTED for takeaway.");
        var expected = order.FulfillmentMethod switch {
            FulfillmentMethods.HomeDelivery => "DELIVERED",
            FulfillmentMethods.Takeaway => "COLLECTED",
            _ => throw new FulfillmentException(409, "invalid_fulfillment_method", "The order fulfillment method needs review.")
        };
        if (day.Status is "DELIVERED" or "COLLECTED") {
            if (day.Status == status) return false;
            throw new FulfillmentException(409, "fulfillment_conflict", "This service day has already been fulfilled with a different status.");
        }
        if (status != expected)
            throw new FulfillmentException(400, "fulfillment_method_mismatch", $"This order requires {expected}.");
        if (!DailyFulfillmentTracking.IsEligible(day, order))
            throw new FulfillmentException(409, "order_not_eligible", "This order is not eligible for fulfillment on this service date.");
        if (day.Status is not ("SCHEDULED" or "PLANNED" or "CONFIRMED"))
            throw new FulfillmentException(409, "invalid_fulfillment_transition", $"Cannot fulfill a service day with status {day.Status}.");
        day.Status = status;
        day.FulfilledAt = now.ToUniversalTime();
        day.FulfilledBy = actor;
        day.UpdatedAt = now.ToUniversalTime();
        day.UpdatedBy = actor;
        day.Revision++;
        return true;
    }
}
