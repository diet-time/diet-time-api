using System.Diagnostics;
using DietTime.Application;
using DietTime.Contracts;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Serilog.Context;

namespace DietTime.Meal.Api.Middleware;

public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context) { const string header = "X-Correlation-ID"; var id = context.Request.Headers[header].FirstOrDefault() ?? Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"); context.Response.Headers[header] = id; using (LogContext.PushProperty("CorrelationId", id)) await next(context); }
}
public sealed class SecurityHeadersMiddleware(RequestDelegate next, IHostEnvironment environment)
{
    private const string ApiContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
    private const string SwaggerContentSecurityPolicy =
        "default-src 'none'; " +
        "script-src 'self' 'unsafe-inline'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "connect-src 'self'; " +
        "frame-ancestors 'none'";

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";

        var isSwaggerRequest = !environment.IsProduction()
            && context.Request.Path.StartsWithSegments("/swagger", StringComparison.OrdinalIgnoreCase);
        context.Response.Headers["Content-Security-Policy"] = isSwaggerRequest
            ? SwaggerContentSecurityPolicy
            : ApiContentSecurityPolicy;

        await next(context);
    }
}
public sealed class ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger, IHostEnvironment environment)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context); }
        catch (Exception ex)
        {
            if (ex is TemplateDayException templateDay)
            {
                logger.LogWarning(ex, "Template weekday request rejected");
                context.Response.StatusCode = templateDay.StatusCode;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new TemplateDayErrorResponse(templateDay.Code, templateDay.Message));
                return;
            }
            var (status, title) = ex switch { ValidationException => (400, "Validation failed"), UnsupportedLanguageException => (400, "Unsupported language"), ArgumentException => (400, "Invalid request"), DbUpdateConcurrencyException => (409, "Concurrency conflict"), DbUpdateException => (409, "Database conflict"), InvalidOperationException => (422, "Request cannot be processed"), _ => (500, "An unexpected error occurred") };
            if (status >= 500) logger.LogError(ex, "Unhandled request failure"); else logger.LogWarning(ex, "Request rejected");
            var details = new ProblemDetails { Status = status, Title = title, Detail = status < 500 || environment.IsDevelopment() ? ex.Message : "The server could not complete the request.", Instance = context.Request.Path }; details.Extensions["correlationId"] = context.Response.Headers["X-Correlation-ID"].ToString();
            if (ex is ValidationException validation) details.Extensions["errors"] = validation.Errors.Select(e => new { field = e.PropertyName, message = e.ErrorMessage });
            context.Response.StatusCode = status; context.Response.ContentType = "application/problem+json"; await context.Response.WriteAsJsonAsync(details);
        }
    }
}
