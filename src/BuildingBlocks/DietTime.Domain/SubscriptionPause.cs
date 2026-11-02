namespace DietTime.Domain;

public sealed class SubscriptionPause : Entity
{
    public Guid OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public Guid SubscriptionId { get; set; }
    public DateOnly PauseStartDate { get; set; }
    public DateOnly ResumeDate { get; set; }
    public DateOnly RequestedResumeDate { get; set; }
    public string Status { get; set; } = "CONFIRMED";
    public string? Reason { get; set; }
    public string ScheduleChangesJson { get; set; } = "[]";
    public Guid? ResumeCommandId { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
