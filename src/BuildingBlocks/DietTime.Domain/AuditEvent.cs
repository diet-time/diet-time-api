namespace DietTime.Domain;

public sealed class AuditEvent : Entity
{
    public DateTimeOffset OccurredAt { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? ActorName { get; set; }
    public string Source { get; set; } = "API";
    public string HttpMethod { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string Action { get; set; } = "";
    public int StatusCode { get; set; }
    public string Outcome { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public long DurationMs { get; set; }
    public ICollection<AuditEventTarget> Targets { get; set; } = [];
}

public sealed class AuditEventTarget : Entity
{
    public Guid AuditEventId { get; set; }
    public AuditEvent AuditEvent { get; set; } = null!;
    public string TargetType { get; set; } = "";
    public string TargetId { get; set; } = "";
}
