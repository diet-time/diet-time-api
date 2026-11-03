using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Persistence;

public sealed class AdminCustomerService(DietTimeDbContext db, TimeProvider clock)
{
    private sealed class Contact
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = "";
        public string? Phone { get; init; }
        public string? Email { get; init; }
        public DateTimeOffset? RegisteredAt { get; init; }
        public bool IsActive { get; init; }
        public string PreferredLanguage { get; init; } = "en";
    }

    private IQueryable<Contact> Contacts() =>
        from profile in db.CustomerProfiles.AsNoTracking()
        join user in db.Users.AsNoTracking() on profile.UserId equals user.Id into users
        from user in users.DefaultIfEmpty()
        join person in db.UserProfiles.AsNoTracking() on profile.UserId equals person.UserId into people
        from person in people.DefaultIfEmpty()
        select new Contact
        {
            Id = profile.Id,
            Name = person != null && (person.FirstName + " " + person.LastName).Trim() != ""
                ? (person.FirstName + " " + person.LastName).Trim()
                : profile.PreferredName != null && profile.PreferredName.Trim() != "" ? profile.PreferredName.Trim()
                : user != null && user.UserName != null ? user.UserName : "Name not recorded",
            Phone = user != null && user.PhoneNumber != null && user.PhoneNumber != "" ? user.PhoneNumber : person != null ? person.Mobile : null,
            Email = user != null ? user.Email : null,
            RegisteredAt = profile.CreatedAt == default ? null : profile.CreatedAt,
            IsActive = profile.IsActive && (person == null || person.IsActive && person.Status == "ACTIVE"),
            PreferredLanguage = profile.PreferredLanguage
        };

    public async Task<AdminCustomerListResponse> ListAsync(string? search, int page, int size, CancellationToken ct, string sort = "name_asc")
    {
        var query = Contacts();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(c => c.Name.ToLower().Contains(term) ||
                c.Phone != null && c.Phone.ToLower().Contains(term) || c.Email != null && c.Email.ToLower().Contains(term));
        }
        var total = await query.CountAsync(ct);
        var ordered = sort switch
        {
            "name_desc" => query.OrderByDescending(c => c.Name).ThenBy(c => c.Id),
            "booked_desc" => query.OrderByDescending(c => db.Orders.Count(o => o.CustomerProfileId == c.Id &&
                (o.Status == "CONFIRMED" || o.Status == "ACTIVE" || o.Status == "COMPLETED"))).ThenBy(c => c.Name).ThenBy(c => c.Id),
            "latest_order_desc" => query.OrderByDescending(c => db.Orders.Any(o => o.CustomerProfileId == c.Id))
                .ThenByDescending(c => db.Orders.Where(o => o.CustomerProfileId == c.Id).Select(o => (DateTimeOffset?)o.PlacedAt).Max())
                .ThenBy(c => c.Name).ThenBy(c => c.Id),
            "newest_desc" => query.OrderByDescending(c => c.RegisteredAt.HasValue).ThenByDescending(c => c.RegisteredAt).ThenBy(c => c.Id),
            _ => query.OrderBy(c => c.Name).ThenBy(c => c.Id)
        };
        var contacts = await ordered.Skip((page - 1) * size).Take(size).ToArrayAsync(ct);
        return new(await SummariesAsync(contacts, ct), total, page, size);
    }

    public async Task<AdminCustomerResponse?> GetAsync(Guid id, CancellationToken ct)
    {
        var contact = await Contacts().SingleOrDefaultAsync(c => c.Id == id, ct);
        return contact == null ? null : (await SummariesAsync([contact], ct))[0];
    }

    public Task<bool> ExistsAsync(Guid id, CancellationToken ct) => db.CustomerProfiles.AsNoTracking().AnyAsync(c => c.Id == id, ct);

    private async Task<AdminCustomerResponse[]> SummariesAsync(Contact[] contacts, CancellationToken ct)
    {
        if (contacts.Length == 0) return [];
        var ids = contacts.Select(c => c.Id).ToArray();
        // Aggregate orders, never their meals or service days, so each purchased plan counts once.
        var totals = await db.Orders.AsNoTracking().Where(o => ids.Contains(o.CustomerProfileId)).GroupBy(o => o.CustomerProfileId)
            .Select(g => new
            {
                Id = g.Key, Total = g.Count(),
                Booked = g.Count(o => o.Status == "CONFIRMED" || o.Status == "ACTIVE" || o.Status == "COMPLETED"),
                Pending = g.Count(o => o.Status == "PENDING_APPROVAL" || o.Status == "PENDING"),
                Cancelled = g.Count(o => o.Status == "CANCELLED"), Rejected = g.Count(o => o.Status == "REJECTED"),
                Latest = g.Max(o => o.PlacedAt)
            }).ToDictionaryAsync(g => g.Id, ct);
        var today = SubscriptionMenuRules.QatarToday(clock.GetUtcNow());
        var eligible = await db.Orders.AsNoTracking().Where(OrderWorkflowPolicy.FulfillmentFilter)
            .Where(o => ids.Contains(o.CustomerProfileId) && o.StartDate <= today && o.EndDate >= today)
            .Select(o => new { o.Id, o.CustomerProfileId }).ToArrayAsync(ct);
        var orderIds = eligible.Select(o => o.Id).ToArray();
        var paused = (await db.Set<SubscriptionPause>().AsNoTracking().Where(p => orderIds.Contains(p.OrderId) &&
            p.Status == "CONFIRMED" && p.PauseStartDate <= today && p.ResumeDate > today)
            .Select(p => p.OrderId).Distinct().ToArrayAsync(ct)).ToHashSet();
        return contacts.Select(c =>
        {
            var total = totals.GetValueOrDefault(c.Id);
            var current = eligible.Where(o => o.CustomerProfileId == c.Id).ToArray();
            return new AdminCustomerResponse(c.Id, c.Name, c.Phone, c.Email, c.RegisteredAt, c.IsActive, c.PreferredLanguage,
                total?.Total ?? 0, total?.Booked ?? 0, current.Count(o => !paused.Contains(o.Id)), current.Count(o => paused.Contains(o.Id)),
                total?.Pending ?? 0, total?.Cancelled ?? 0, total?.Rejected ?? 0, total?.Latest);
        }).ToArray();
    }
}
