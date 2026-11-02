namespace DietTime.Contracts;

public sealed record AdminCustomerResponse(Guid Id, string Name, string? Phone, string? Email,
    DateTimeOffset? RegisteredAt, bool IsActive, string PreferredLanguage,
    int TotalOrders, int BookedPlans, int ActivePlans, int PausedPlans, int PendingOrders,
    int CancelledOrders, int RejectedOrders, DateTimeOffset? LatestOrderAt);

public sealed record AdminCustomerListResponse(IReadOnlyList<AdminCustomerResponse> Items,
    int TotalCount, int PageNumber, int PageSize);
