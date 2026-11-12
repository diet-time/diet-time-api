namespace DietTime.Contracts;
public sealed record PackageScheduleSummaryResponse(string DisplayStatus, string? PauseApprovalStatus,
    DateOnly? PauseStartDate, DateOnly? ResumeDate, DateOnly RevisedEndDate, int RemainingServiceDays,
    DateOnly? NextScheduledDelivery, bool TomorrowScheduled, DateTimeOffset ServerNow)
{
    public DateOnly? ServiceStartDate { get; init; }
    public IReadOnlyList<DateOnly>? ServiceDates { get; init; }
    public bool? DashboardEligible { get; init; }
    public string? DashboardStatus { get; init; }
    public string? DashboardExclusionReason { get; init; }
    public string? DashboardScheduleSource { get; init; }
}
public sealed record PausePreviewRequest(DateOnly PauseStartDate, DateOnly ResumeDate, bool MoveRemaining = false);
public sealed record PausePackageRequest(Guid CommandId, long ExpectedRowVersion, string ScheduleVersion,
    DateOnly PauseStartDate, DateOnly ResumeDate, string? Reason, bool MoveRemaining = false);
public sealed record ResumePackageRequest(Guid CommandId, Guid PauseId, long ExpectedRowVersion, DateOnly? ResumeDate = null);
public sealed record PackagePauseResponse(Guid Id, DateOnly PauseStartDate, DateOnly ResumeDate, string Status, string? Reason);
public sealed record PausePreviewResponse(long RowVersion, string ScheduleVersion, DateOnly EarliestPauseDate,
    DateOnly PauseStartDate, DateOnly ResumeDate, int AffectedServiceDays, int RescheduledServiceDays,
    int PreservedCommittedDays, DateOnly ProjectedEndDate)
{
    public int RemainingServiceDays { get; init; }
    public DateOnly? ActualRestartDate { get; init; }
}
public sealed record PackageManagementResponse(Guid OrderId, long RowVersion, DateTimeOffset ServerNow,
    string Status, int RemainingServiceDays, DateOnly? NextScheduledDelivery, DateOnly ExpectedEndDate,
    IReadOnlyList<DateOnly> EligiblePauseDates, string? PauseUnavailableReason,
    PackagePauseResponse? CurrentPause, bool CanResumeEarly)
{
    public string? DisplayStatus { get; init; }
    public string? PauseApprovalStatus { get; init; }
    public DateOnly? FirstDeliveryAfterPause { get; init; }
    public PackageChangeResponse? LatestRequest { get; init; }
}

public sealed record PackageChangeResponse(Guid Id, string Type, string Status, DateOnly? PauseStartDate,
    DateOnly? ResumeDate, DateOnly? ProjectedEndDate, string? Reason, DateTimeOffset CreatedAt,
    DateTimeOffset? ReviewedAt, string? ReviewReason);
public sealed record StaffPackageChangeResponse(Guid OrderId, string OrderNumber, string PlanName,
    string CustomerName, PackageChangeResponse Request);
public sealed record PackageChangeReviewRequest(bool Approve, string? Reason = null);
public sealed record StaffPackageChangeListResponse(IReadOnlyList<StaffPackageChangeResponse> Items,
    int TotalCount, int PageNumber, int PageSize);
