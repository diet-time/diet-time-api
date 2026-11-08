using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Asp.Versioning;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DietTime.Meal.Api.Controllers;

[ApiController]
[ApiVersion(1)]
[Authorize]
[Route("api/v{version:apiVersion}/orders")]
public sealed class OrdersController(IOrderService orders, StaffOrderService workflow, IPackageManagementService packages) : ControllerBase
{
    [HttpGet("{orderId:guid}/package-management")]
    public Task<IActionResult> Package(Guid orderId, CancellationToken ct) => PackageAction(u => packages.GetPackageAsync(orderId, u, ct));
    [HttpGet("{orderId:guid}/pauses")]
    public Task<IActionResult> Pauses(Guid orderId, CancellationToken ct) => PackageAction(u => packages.GetPausesAsync(orderId, u, ct));
    [HttpPost("{orderId:guid}/pause/preview")]
    public Task<IActionResult> PreviewPause(Guid orderId, PausePreviewRequest request, CancellationToken ct) => PackageAction(u => packages.PreviewPauseAsync(orderId, u, request, ct));
    [HttpPost("{orderId:guid}/pause")]
    public Task<IActionResult> Pause(Guid orderId, PausePackageRequest request, CancellationToken ct) => PackageAction(u => packages.RequestPauseAsync(orderId, u, request, ct));
    [HttpPost("{orderId:guid}/resume")]
    public Task<IActionResult> Resume(Guid orderId, ResumePackageRequest request, CancellationToken ct) => PackageAction(u => packages.RequestResumeAsync(orderId, u, request, ct));
    private async Task<IActionResult> PackageAction<T>(Func<Guid, Task<T>> action)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        try { return Ok(await action(userId)); }
        catch (UpcomingMealException error) { return ProblemResponse(error.StatusCode, error.Code, error.Message); }
        catch (ArgumentOutOfRangeException) { return ProblemResponse(400, "invalid_resume_date", "Choose a supported resume date."); }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException error) when (error.InnerException is Npgsql.PostgresException { SqlState: "23505" }) { return ProblemResponse(409, "idempotency_conflict", "This command conflicts with the current package schedule. Refresh before continuing."); }
        catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException) { return ProblemResponse(409, "schedule_changed", "This package changed. Refresh before continuing."); }
    }

    [HttpPost("{orderId:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid orderId, OrderReviewRequest request, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        try { return Ok((await workflow.ChangeAsync(orderId, "cancel", request.ExpectedRowVersion, request.Reason, userId, ct, customerUserId: userId)).Order); }
        catch (OrderReviewException error) { return ProblemResponse(error.Status, error.Code, error.Message); }
    }
    /// <summary>Validates and places a meal-plan order atomically.</summary>
    /// <remarks>
    /// Send a stable <c>Idempotency-Key</c> header for a checkout attempt. Reusing it returns
    /// the original order. Example body:
    /// <code>{ "customerProfileId": "a812c2c3-4378-4c35-a586-21cbec880832", "mealPlanTemplateId": "f60bc162-3114-4efd-9458-46e6838fed72", "mealPlanPriceId": "7b392aaf-89f4-4685-a4c6-022fe432ed8e", "customerAddressId": "9615fc85-207d-4e17-93b0-f74722ec55cb", "deliveryTimeSlotId": "3855142d-0846-4319-b976-f16cab3468ab", "startDate": "2026-08-11", "deliveryDays": [2,3,4,5,6], "meals": [{ "mealTypeId": "11111111-1111-1111-1111-111111111111", "quantity": 1 }], "couponCode": null }</code>
    /// </remarks>
    [HttpPost]
    [Consumes("application/json")]
    [Produces("application/json", "application/problem+json")]
    [ProducesResponseType(typeof(PlaceOrderResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Place(
        PlaceOrderRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 100)
            return ProblemResponse(400, "invalid_idempotency_key",
                "Idempotency-Key is required and must contain at most 100 characters.");

        var result = await orders.PlaceAsync(request, idempotencyKey, userId, cancellationToken);
        if (result.Order is not null)
        {
            if (result.Status == PlaceOrderStatus.Replayed)
                Response.Headers["Idempotent-Replayed"] = "true";
            return CreatedAtAction(nameof(Get), new { version = "1", orderId = result.Order.Id }, result.Order);
        }

        return result.Status switch
        {
            PlaceOrderStatus.CustomerNotFound or PlaceOrderStatus.TemplateNotFound or
                PlaceOrderStatus.PriceNotFound or PlaceOrderStatus.AddressNotFound or
                PlaceOrderStatus.DeliveryTimeSlotNotFound => ProblemResponse(404,
                    result.ErrorCode ?? result.Status.ToString().ToSnakeCase(), result.Detail!),
            PlaceOrderStatus.TemplateUnavailable or PlaceOrderStatus.PriceUnavailable or
                PlaceOrderStatus.IdempotencyConflict or PlaceOrderStatus.PriceChanged =>
                ProblemResponse(409, result.ErrorCode ?? result.Status.ToString().ToSnakeCase(), result.Detail!),
            _ => ProblemResponse(400, result.ErrorCode ?? result.Status.ToString().ToSnakeCase(), result.Detail!)
        };
    }

    /// <summary>Returns an order from its stored pricing, plan, address, and time-slot snapshots.</summary>
    [HttpGet("{orderId:guid}")]
    [ProducesResponseType(typeof(PlaceOrderResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid orderId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var order = await orders.GetAsync(orderId, userId, cancellationToken);
        return order is null
            ? ProblemResponse(404, "order_not_found", "Order was not found.")
            : Ok(order);
    }

    /// <summary>Initial cards only: all current plans and three recent orders; no meal calendars.</summary>
    [HttpGet("/api/v{version:apiVersion}/customer-profiles/{customerProfileId:guid}/orders/dashboard")]
    public async Task<IActionResult> Dashboard(Guid customerProfileId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        Response.Headers.CacheControl = "no-store";
        var response = await orders.GetDashboardAsync(customerProfileId, userId, cancellationToken);
        return response is null
            ? ProblemResponse(404, "customer_profile_not_found", "Customer profile was not found or is not accessible.")
            : Ok(response);
    }

    /// <summary>Returns the authenticated customer's orders, newest first.</summary>
    [HttpGet("/api/v{version:apiVersion}/customer-profiles/{customerProfileId:guid}/orders")]
    [ProducesResponseType(typeof(CustomerOrdersResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCustomerOrders(
        Guid customerProfileId,
        [FromQuery] string? status = null,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        if (pageNumber < 1 || pageSize is < 1 or > 100)
            return ProblemResponse(400, "invalid_pagination",
                "pageNumber must be at least 1 and pageSize must be between 1 and 100.");
        if (status?.Trim().Length > 30)
            return ProblemResponse(400, "invalid_status", "status must contain at most 30 characters.");

        var response = await orders.GetCustomerOrdersAsync(
            customerProfileId, userId, status, pageNumber, pageSize, cancellationToken);
        return response is null
            ? ProblemResponse(404, "customer_profile_not_found",
                "Customer profile was not found or is not accessible.")
            : Ok(response);
    }

    private ObjectResult ProblemResponse(int status, string code, string detail)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = status switch { 404 => "Resource not found", 409 => "Conflict", _ => "Invalid request" },
            Detail = detail,
            Instance = HttpContext.Request.Path
        };
        problem.Extensions["code"] = code;
        return StatusCode(status, problem);
    }

    private bool TryGetUserId(out Guid userId)
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? User.FindFirstValue("sub");
        return Guid.TryParse(value, out userId);
    }
}

internal static class OrderApiStringExtensions
{
    public static string ToSnakeCase(this string value) => string.Concat(value.Select((character, index) =>
        char.IsUpper(character) && index > 0 ? "_" + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString()));
}
