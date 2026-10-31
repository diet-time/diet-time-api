using System.Globalization;
using Asp.Versioning;
using DietTime.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Meal.Api.Controllers;

[ApiController]
[ApiVersion(1)]
[Authorize(Roles = "Admin")]
[Route("api/admin/audit")]
[Route("api/v{version:apiVersion}/admin/audit")]
public sealed class AuditController(DietTimeDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(int page = 1, int pageSize = 25, string? entityType = null,
        string? action = null, Guid? userId = null, string? source = null, string? outcome = null, string? user = null,
        string? from = null, string? until = null, CancellationToken ct = default)
    {
        if (page < 1 || page > 1000000 || pageSize is < 1 or > 100 || entityType?.Length > 100 || action?.Length > 100 || user?.Length > 200)
            return BadRequest();
        var query = db.AuditEvents.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(entityType)) query = query.Where(e => EF.Functions.ILike(e.EntityType, "%" + entityType.Trim() + "%"));
        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(e => EF.Functions.ILike(e.Action, "%" + action.Trim() + "%"));
        if (userId.HasValue) query = query.Where(e => e.ActorUserId == userId);
        if (!string.IsNullOrWhiteSpace(user))
        {
            if (Guid.TryParse(user, out var actorId)) query = query.Where(e => e.ActorUserId == actorId);
            else query = query.Where(e => EF.Functions.ILike(e.ActorName ?? "Anonymous", "%" + user.Trim() + "%"));
        }
        if (!string.IsNullOrWhiteSpace(source))
        {
            if (source is not ("API" or "FLUTTER" or "REACT_ADMIN")) return BadRequest();
            query = query.Where(e => e.Source == source);
        }
        if (!string.IsNullOrWhiteSpace(outcome))
        {
            if (outcome is not ("SUCCEEDED" or "REJECTED" or "FAILED")) return BadRequest();
            query = query.Where(e => e.Outcome == outcome);
        }
        // Day filters are interpreted in Qatar; storage remains UTC timestamptz.
        DateOnly? first = null, last = null;
        if (from is not null)
        {
            if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date == DateOnly.MinValue) return BadRequest();
            first = date;
            var start = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3)).ToUniversalTime();
            query = query.Where(e => e.OccurredAt >= start);
        }
        if (until is not null)
        {
            if (!DateOnly.TryParseExact(until, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date == DateOnly.MaxValue) return BadRequest();
            last = date;
            var end = new DateTimeOffset(date.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3)).ToUniversalTime();
            query = query.Where(e => e.OccurredAt < end);
        }
        if (first.HasValue && last.HasValue && first > last) return BadRequest();
        var count = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).Include(e => e.Targets).AsSplitQuery().ToArrayAsync(ct);
        return Ok(new
        {
            items = rows.Select(e => new {
                e.Id, e.EntityType, entityId = e.Targets.FirstOrDefault()?.TargetId ?? "", e.Action,
                changedBy = e.ActorName ?? e.ActorUserId?.ToString() ?? "Anonymous", userId = e.ActorUserId,
                changedAt = e.OccurredAt, e.Source, e.Outcome, e.StatusCode, e.HttpMethod, e.Endpoint, e.CorrelationId,
                targets = e.Targets.Select(t => new { type = t.TargetType, id = t.TargetId })
            }), page, pageSize, totalCount = count, totalPages = (int)Math.Ceiling(count / (double)pageSize)
        });
    }
}
