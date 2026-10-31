using System.Diagnostics;
using System.Security.Claims;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Meal.Api.Middleware;

public sealed class AuditRequestCapture
{
    public Guid? AuthenticatedUserId { get; set; }
    public List<(string Type, string Id)> Targets { get; } = [];
}

// Inspect only server-produced identifiers, never a request/response body snapshot.
public sealed class AuditResultFilter(AuditRequestCapture capture, ILogger<AuditResultFilter> logger) : IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        try
        {
            if (context.Result is Microsoft.AspNetCore.Mvc.ObjectResult result && result.StatusCode is null or < 400)
            {
                var value = result.Value;
                var data = value?.GetType().GetProperty("Data")?.GetValue(value) ?? value;
                if (data is DietTime.Contracts.AuthSessionResponse session)
                    capture.AuthenticatedUserId = session.User.Id;
                var id = data?.GetType().GetProperty("Id")?.GetValue(data);
                if (id is Guid guid) capture.Targets.Add(("result", guid.ToString()));
            }
        }
        catch (Exception error) { logger.LogWarning(error, "Unable to extract audit result identifiers"); }
        await next();
    }
}

public sealed class AuditEventWriter(IServiceScopeFactory scopeFactory, ILogger<AuditEventWriter> logger)
{
    public async Task WriteAsync(AuditEvent entry)
    {
        try
        {
            // Independent scope: failed business entities must never be saved again.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DietTimeDbContext>();
            if (entry.ActorUserId is { } actor)
            {
                entry.ActorName = await db.UserProfiles.AsNoTracking().Where(p => p.UserId == actor)
                    .Select(p => (p.FirstName + " " + p.LastName).Trim()).FirstOrDefaultAsync(timeout.Token);
                if (string.IsNullOrWhiteSpace(entry.ActorName))
                    entry.ActorName = await db.CustomerProfiles.AsNoTracking().Where(p => p.UserId == actor)
                        .Select(p => p.PreferredName).FirstOrDefaultAsync(timeout.Token);
            }
            if (entry.ActorName?.Length > 200) entry.ActorName = entry.ActorName[..200];
            db.AuditEvents.Add(entry);
            await db.SaveChangesAsync(timeout.Token);
        }
        catch (Exception error)
        {
            // An audit-store outage cannot roll back an already placed/approved order.
            // Retain safe metadata in application logs so missing audit storage is visible.
            logger.LogError(error, "AUDIT_WRITE_FAILED EventId={EventId} At={OccurredAt} Actor={Actor} Source={Source} Entity={Entity} Action={Action} Method={Method} Endpoint={Endpoint} Targets={Targets} Status={Status} Correlation={Correlation}",
                entry.Id, entry.OccurredAt, entry.ActorUserId, entry.Source, entry.EntityType, entry.Action,
                entry.HttpMethod, entry.Endpoint, string.Join(",", entry.Targets.Select(t => t.TargetType + ":" + t.TargetId)), entry.StatusCode, entry.CorrelationId);
        }
    }
}

public sealed class RequestAuditMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, AuditRequestCapture capture, AuditEventWriter writer, TimeProvider clock)
    {
        if (!context.Request.Path.StartsWithSegments("/api") || HttpMethods.IsOptions(context.Request.Method))
        { await next(context); return; }
        var started = Stopwatch.GetTimestamp();
        var occurredAt = clock.GetUtcNow();
        var escaped = false;
        try { await next(context); }
        catch { escaped = true; throw; }
        finally
        {
            var descriptor = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "[unmatched API route]";
            var actor = capture.AuthenticatedUserId;
            if (actor is null && context.User.Identity?.IsAuthenticated == true &&
                Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub"), out var userId)) actor = userId;
            var status = escaped ? 500 : context.Response.StatusCode;
            var source = context.Request.Headers["X-Client-App"].ToString().ToUpperInvariant() switch
                { "FLUTTER" => "FLUTTER", "REACT_ADMIN" => "REACT_ADMIN", _ => "API" };
            var entry = new AuditEvent
            {
                Id = Guid.NewGuid(), OccurredAt = occurredAt, ActorUserId = actor, Source = source,
                HttpMethod = context.Request.Method is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" ? context.Request.Method : "OTHER",
                Endpoint = route.Length <= 300 ? route : route[..300],
                EntityType = descriptor?.ControllerName ?? "API", Action = descriptor?.ActionName ?? "Request",
                StatusCode = status, Outcome = status >= 500 ? "FAILED" : status >= 400 ? "REJECTED" : "SUCCEEDED",
                CorrelationId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
                DurationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds
            };
            foreach (var pair in context.Request.RouteValues)
                if (Guid.TryParse(pair.Value?.ToString(), out var id)) capture.Targets.Add((pair.Key, id.ToString()));
            foreach (var target in capture.Targets.Distinct())
                entry.Targets.Add(new AuditEventTarget { Id = Guid.NewGuid(), TargetType = target.Type, TargetId = target.Id });
            await writer.WriteAsync(entry);
        }
    }
}
