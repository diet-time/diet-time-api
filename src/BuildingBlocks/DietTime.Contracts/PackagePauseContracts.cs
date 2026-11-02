namespace DietTime.Contracts;
public sealed record PausePreviewRequest(DateOnly PauseStartDate, DateOnly ResumeDate);
public sealed record PausePackageRequest(Guid CommandId, long ExpectedRowVersion, string ScheduleVersion,
    DateOnly PauseStartDate, DateOnly ResumeDate, string? Reason);
public sealed record ResumePackageRequest(Guid CommandId, Guid PauseId, long ExpectedRowVersion);
public sealed record PackagePauseResponse(Guid Id, DateOnly PauseStartDate, DateOnly ResumeDate, string Status, string? Reason);
public sealed record PausePreviewResponse(long RowVersion, string ScheduleVersion, DateOnly EarliestPauseDate,
    DateOnly PauseStartDate, DateOnly ResumeDate, int AffectedServiceDays, int RescheduledServiceDays,
    int PreservedCommittedDays, DateOnly ProjectedEndDate);
public sealed record PackageManagementResponse(Guid OrderId, long RowVersion, DateTimeOffset ServerNow,
    string Status, int RemainingServiceDays, DateOnly? NextScheduledDelivery, DateOnly ExpectedEndDate,
    IReadOnlyList<DateOnly> EligiblePauseDates, string? PauseUnavailableReason,
    PackagePauseResponse? CurrentPause, bool CanResumeEarly);
