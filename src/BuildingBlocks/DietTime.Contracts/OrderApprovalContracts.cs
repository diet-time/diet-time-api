namespace DietTime.Contracts;

public sealed record OrderReviewResponse(long RowVersion, Guid? ApprovedBy, DateTimeOffset? ApprovedAt,
    Guid? RejectedBy, DateTimeOffset? RejectedAt, string? RejectionReason, string? HoldReason,
    bool RequiresPrepayment, bool AwaitingPayment, bool CanFulfill, string? FulfillmentBlockedReason);

public sealed record OrderReviewRequest(long ExpectedRowVersion, string? Reason = null);
public sealed record ReviseOrderDatesRequest(long ExpectedRowVersion, DateOnly StartDate, IReadOnlyList<int> ServiceWeekdays, string Reason);
public sealed record RecordOrderPaymentRequest(long ExpectedRowVersion, string PaymentStatus, string Reason);
public sealed record OrderHistoryResponse(Guid Id, string? PreviousStatus, string Status, Guid? ChangedBy,
    DateTimeOffset ChangedAt, string? Reason, string? PreviousPaymentStatus, string? PaymentStatus);
public sealed record StaffOrderResponse(PlaceOrderResponse Order, string CustomerName, string? CustomerPhone,
    IReadOnlyList<OrderHistoryResponse> History);
public sealed record StaffOrderListResponse(IReadOnlyList<StaffOrderResponse> Items, int TotalCount, int PageNumber, int PageSize);
