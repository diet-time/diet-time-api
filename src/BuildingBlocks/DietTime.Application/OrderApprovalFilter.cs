namespace DietTime.Application;

public static class OrderApprovalFilter
{
    // Read-only approval grouping. Stored lifecycle, fulfillment and payment states are never rewritten.
    public static string[] MatchingStatuses(string status) => status switch
    {
        "PENDING_APPROVAL" => ["PENDING_APPROVAL", "PENDING"],
        "CONFIRMED" => ["CONFIRMED", "ACTIVE", "COMPLETED"],
        _ => [status]
    };
}
