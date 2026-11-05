namespace DietTime.Domain;

public sealed class PackageChangeRequest : Entity
{
    public Guid OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public string Type { get; set; } = "PAUSE";
    public string Status { get; set; } = "PENDING_APPROVAL";
    public Guid RequestedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateOnly? PauseStartDate { get; set; }
    public DateOnly? ResumeDate { get; set; }
    public DateOnly? ProjectedEndDate { get; set; }
    public string? Reason { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public Guid? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewReason { get; set; }
}
