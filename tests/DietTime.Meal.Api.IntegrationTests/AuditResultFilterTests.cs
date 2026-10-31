using DietTime.Contracts;
using DietTime.Meal.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed class AuditResultFilterTests
{
    [Fact]
    public async Task Successful_sign_in_uses_the_server_authenticated_identity_without_retaining_tokens()
    {
        var id = Guid.NewGuid();
        var session = new AuthSessionResponse("secret-access-token", DateTimeOffset.UtcNow.AddMinutes(10),
            "secret-refresh-token", DateTimeOffset.UtcNow.AddDays(7), new AuthUserResponse(id, "user@example.test", "Name", ["Customer"]));
        var capture = new AuditRequestCapture();
        await Execute(capture, new OkObjectResult(ApiResponse<AuthSessionResponse>.Ok(session)));
        Assert.Equal(id, capture.AuthenticatedUserId);
        Assert.Empty(capture.Targets);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(capture));
    }

    [Fact]
    public async Task Audit_metadata_failure_does_not_interrupt_a_business_response()
    {
        var capture = new AuditRequestCapture();
        await Execute(capture, new OkObjectResult(new ThrowingPayload()));
        Assert.Empty(capture.Targets);
    }

    private static async Task Execute(AuditRequestCapture capture, IActionResult result)
    {
        var context = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        var filters = new List<IFilterMetadata>();
        var controller = new object();
        var invoked = false;
        await new AuditResultFilter(capture, NullLogger<AuditResultFilter>.Instance).OnResultExecutionAsync(
            new ResultExecutingContext(context, filters, result, controller), () => {
                invoked = true;
                return Task.FromResult(new ResultExecutedContext(context, filters, result, controller));
            });
        Assert.True(invoked);
    }

    private sealed class ThrowingPayload
    {
        public object Data => throw new InvalidOperationException("Failed metadata getter");
    }
}
