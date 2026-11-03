using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Persistence;

public sealed partial class OperationsDashboardService
{
    private async Task<DashboardCustomerInsightsDto> BuildCustomerInsightsAsync(DateOnly date, CancellationToken ct)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.BusinessTimeZone);
        var end = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(date.AddDays(1).ToDateTime(TimeOnly.MinValue), zone));
        var startDate = date.AddDays(-29);
        var start = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(startDate.ToDateTime(TimeOnly.MinValue), zone));
        // Match the customer directory: one accepted purchase, never a meal or delivery-day count.
        var bookings = db.Orders.AsNoTracking().Where(o => o.PlacedAt < end &&
            (o.Status == OrderStatuses.Confirmed || o.Status == OrderStatuses.Active || o.Status == OrderStatuses.Completed))
            .GroupBy(o => o.CustomerProfileId).Select(g => new
            {
                CustomerId = g.Key, Orders = g.Count(), First = g.Min(o => o.PlacedAt), Latest = g.Max(o => o.PlacedAt)
            });
        var totals = await bookings.GroupBy(_ => 1).Select(g => new
        {
            Customers = g.Count(), Repeat = g.Count(c => c.Orders >= 2),
            New = g.Count(c => c.First >= start), Orders = g.Sum(c => c.Orders)
        }).SingleOrDefaultAsync(ct);
        var top = await bookings.OrderByDescending(c => c.Orders).ThenByDescending(c => c.Latest)
            .ThenBy(c => c.CustomerId).Take(5).ToArrayAsync(ct);
        var ids = top.Select(c => c.CustomerId).ToArray();
        var names = await (from profile in db.CustomerProfiles.AsNoTracking()
            join person in db.UserProfiles.AsNoTracking() on profile.UserId equals (Guid?)person.UserId into people
            from person in people.DefaultIfEmpty()
            where ids.Contains(profile.Id)
            select new { profile.Id, Name = person != null && (person.FirstName + " " + person.LastName).Trim() != ""
                ? (person.FirstName + " " + person.LastName).Trim()
                : profile.PreferredName != null && profile.PreferredName.Trim() != "" ? profile.PreferredName.Trim() : "Name not recorded" })
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        return new(date, startDate, totals?.Customers ?? 0, totals?.Repeat ?? 0, totals?.New ?? 0, totals?.Orders ?? 0,
            top.Select(c => new DashboardTopCustomerDto(c.CustomerId, names.GetValueOrDefault(c.CustomerId, "Name not recorded"), c.Orders, c.Latest)).ToArray());
    }
}
