using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DietTime.Meal.Api.Middleware;

// Never log SQL text, parameters, URLs, identity, request or response bodies.
public sealed class PerformanceCommandInterceptor(ILogger<PerformanceCommandInterceptor> logger) : DbCommandInterceptor
{
    private void Record(DbCommand command, CommandEndEventData data)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.CommandText)))[..16];
        var elapsed = data.Duration.TotalMilliseconds;
        Activity.Current?.AddEvent(new ActivityEvent("database.command", tags: new ActivityTagsCollection
        { { "db.query.fingerprint", fingerprint }, { "db.duration_ms", elapsed } }));
        logger.Log(elapsed >= 200 ? LogLevel.Warning : LogLevel.Debug,
            "DB_COMMAND QueryFingerprint={QueryFingerprint} DurationMs={DurationMs} TraceId={TraceId}",
            fingerprint, elapsed, Activity.Current?.TraceId.ToString());
    }
    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData data, DbDataReader result)
    { Record(command, data); return result; }
    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data, DbDataReader result, CancellationToken cancellationToken = default)
    { Record(command, data); return ValueTask.FromResult(result); }
    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData data, object? result)
    { Record(command, data); return result; }
    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData data, object? result, CancellationToken cancellationToken = default)
    { Record(command, data); return ValueTask.FromResult(result); }
    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData data, int result)
    { Record(command, data); return result; }
    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData data, int result, CancellationToken cancellationToken = default)
    { Record(command, data); return ValueTask.FromResult(result); }
    public override void CommandFailed(DbCommand command, CommandErrorEventData data) => Record(command, data);
    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData data, CancellationToken cancellationToken = default)
    { Record(command, data); return Task.CompletedTask; }
}

public sealed class PerformanceActionFilter(ILogger<PerformanceActionFilter> logger) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var started = Stopwatch.GetTimestamp();
        try { await next(); }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            logger.Log(elapsed >= 500 ? LogLevel.Warning : LogLevel.Debug,
                "API_ACTION Action={Action} DurationMs={DurationMs} TraceId={TraceId}",
                context.ActionDescriptor.DisplayName, elapsed, Activity.Current?.TraceId.ToString());
        }
    }
}

public sealed class PerformanceRequestMiddleware(RequestDelegate next, ILogger<PerformanceRequestMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        var failed = false;
        try { await next(context); }
        catch { failed = true; throw; }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
            logger.Log(elapsed >= 500 || failed ? LogLevel.Warning : LogLevel.Debug,
                "API_REQUEST Route={Route} Method={Method} StatusCode={StatusCode} DurationMs={DurationMs} TraceId={TraceId}",
                route, context.Request.Method, failed ? 500 : context.Response.StatusCode,
                elapsed, Activity.Current?.TraceId.ToString());
        }
    }
}
public sealed class PerformanceConnectionInterceptor(ILogger<PerformanceConnectionInterceptor> logger) : DbConnectionInterceptor
{
    private void Record(ConnectionEndEventData data) => logger.Log(
        data.Duration.TotalMilliseconds >= 100 ? LogLevel.Warning : LogLevel.Debug,
        "DB_CONNECTION_OPEN DurationMs={DurationMs} TraceId={TraceId}",
        data.Duration.TotalMilliseconds, Activity.Current?.TraceId.ToString());
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData data) => Record(data);
    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData data, CancellationToken cancellationToken = default)
    { Record(data); return Task.CompletedTask; }
}