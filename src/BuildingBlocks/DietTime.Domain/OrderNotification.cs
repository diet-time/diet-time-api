namespace DietTime.Domain;

// One event per audience, written atomically with the order/status history.
public sealed class OrderNotification : Entity
{
    public Guid OrderId { get; set; }
    public Guid HistoryId { get; set; }
    public string EventCode { get; set; } = "";
    public string Audience { get; set; } = "CUSTOMER";
    public string? Reason { get; set; }
    public string Status { get; set; } = "PENDING";
    public int Attempts { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public string? MessageSid { get; set; }
    public string? LastError { get; set; }
}
