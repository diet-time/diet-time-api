namespace DietTime.Contracts;
public sealed record ServiceDayFulfillmentRequest(string Status);
public sealed record ServiceDayFulfillmentResponse(Guid ServiceDayId, string Status, DateTimeOffset? FulfilledAt, Guid? FulfilledBy, string? StaffDisplayName = null);
