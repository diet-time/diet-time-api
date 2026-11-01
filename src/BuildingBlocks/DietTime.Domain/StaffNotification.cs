namespace DietTime.Domain;
public sealed class StaffNotificationEvent : Entity
{
    public string DeduplicationKey { get; set; } = "";
    public string EventType { get; set; } = "";
    public Guid OrderId { get; set; }
    public Guid? ServiceDayId { get; set; }
    public DateOnly? ServiceDate { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}
public sealed class StaffNotification : Entity
{
    public Guid RecipientUserId { get; set; }
    public string DeduplicationKey { get; set; } = "";
    public string EventType { get; set; } = "";
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public Guid OrderId { get; set; }
    public string OrderNumber { get; set; } = "";
    public Guid? ServiceDayId { get; set; }
    public DateOnly? ServiceDate { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}
