using DietTime.Contracts;
using DietTime.Domain;
namespace DietTime.Application;

public interface IPackageManagementService
{
    Task<PackageManagementResponse> GetPackageAsync(Guid orderId, Guid userId, CancellationToken ct);
    Task<IReadOnlyList<PackagePauseResponse>> GetPausesAsync(Guid orderId, Guid userId, CancellationToken ct);
    Task<PausePreviewResponse> PreviewPauseAsync(Guid orderId, Guid userId, PausePreviewRequest request, CancellationToken ct);
    Task<PackageManagementResponse> RequestPauseAsync(Guid orderId, Guid userId, PausePackageRequest request, CancellationToken ct);
    Task<PackageManagementResponse> RequestResumeAsync(Guid orderId, Guid userId, ResumePackageRequest request, CancellationToken ct);
    Task<StaffPackageChangeListResponse> ListChangeRequestsAsync(int page, int size, CancellationToken ct);
    Task<PackageChangeResponse> ReviewChangeRequestAsync(Guid requestId, Guid reviewer, PackageChangeReviewRequest request, CancellationToken ct);
    Task<PackageManagementResponse> PauseAsync(Guid orderId, Guid userId, PausePackageRequest request, CancellationToken ct);
    Task<PackageManagementResponse> ResumeAsync(Guid orderId, Guid userId, ResumePackageRequest request, CancellationToken ct);
}

public static class PackagePauseRules
{
    public const string CancellationCode = "ORDER_CANCELLATION_NOT_ALLOWED";
    public const string CancellationMessage = "This booking is confirmed. Please contact Diet Time to request cancellation. You may also pause future deliveries.";
    public static bool CancellationBlocked(Order order) =>
        (order.Status is "CONFIRMED" or "ACTIVE" or "PAUSED" ||
         order.ApprovedAt.HasValue && order.Status is not ("CANCELLED" or "REJECTED"));

    public static bool CanMove(SubscriptionServiceDay day, DateTimeOffset now) =>
        day.ServiceDate > SubscriptionMenuRules.QatarToday(now) && day.ServiceDate.DayOfWeek != DayOfWeek.Friday &&
        day.Status is "PLANNED" or "CONFIRMED" && !day.SelectionLocked && day.SelectionStatus != "LOCKED" &&
        day.CustomerConfirmedAt == null && day.AutoFinalizedAt == null && day.FulfilledAt == null &&
        now < SubscriptionMenuRules.TomorrowCutoff(day.ServiceDate) &&
        (day.SelectionCutoffAt == null || now < day.SelectionCutoffAt);

    public static DateOnly MinimumResumeDate(DateTimeOffset now) =>
        SubscriptionMenuRules.QatarToday(now).AddDays(now >= SubscriptionMenuRules.TomorrowCutoff(SubscriptionMenuRules.QatarToday(now).AddDays(1)) ? 2 : 1);

    public static DateOnly MaximumResumeDate(DateTimeOffset now) => SubscriptionMenuRules.QatarToday(now).AddMonths(6);

    public static void ValidateResumeDate(DateOnly date, DateTimeOffset now, IReadOnlySet<int>? weekdays = null)
    {
        if (date < MinimumResumeDate(now) || date > MaximumResumeDate(now) || date.DayOfWeek == DayOfWeek.Friday ||
            weekdays != null && !weekdays.Contains(date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek))
            throw new UpcomingMealException(400, "invalid_resume_date", "Choose an available future restart date within six months.");
    }

    public static DateOnly NextResumeServiceDate(DateOnly date, IReadOnlySet<int> weekdays)
    {
        if (weekdays.Count == 0 || weekdays.Any(d => d is < 1 or > 7 or 5))
            throw new UpcomingMealException(409, "schedule_review_required", "The delivery weekdays need review by Diet Time.");
        while (date.DayOfWeek == DayOfWeek.Friday || !weekdays.Contains(date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek))
            date = date.AddDays(1);
        return date;
    }

    public static IReadOnlyDictionary<Guid, DateOnly> Schedule(IReadOnlyList<SubscriptionServiceDay> moving,
        IReadOnlyList<SubscriptionServiceDay> all, DateOnly resumeDate, IReadOnlySet<int> weekdays)
    {
        if (weekdays.Count == 0 || weekdays.Any(d => d is < 1 or > 7 or 5))
            throw new UpcomingMealException(409, "schedule_review_required", "The delivery weekdays need review by Diet Time.");
        var ids = moving.Select(d => d.Id).ToHashSet();
        var occupied = all.Where(d => !ids.Contains(d.Id)).Select(d => d.ServiceDate).ToHashSet();
        var result = new Dictionary<Guid, DateOnly>();
        var date = resumeDate;
        foreach (var day in moving.OrderBy(d => d.ServiceDate).ThenBy(d => d.Id))
        {
            while (date.DayOfWeek == DayOfWeek.Friday || !weekdays.Contains(date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek) || occupied.Contains(date))
                date = date.AddDays(1);
            result.Add(day.Id, date); occupied.Add(date); date = date.AddDays(1);
        }
        return result;
    }
}
