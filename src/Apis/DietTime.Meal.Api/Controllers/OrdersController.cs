using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Asp.Versioning;
using DietTime.Application;
using DietTime.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DietTime.Meal.Api.Controllers;

[ApiController]
[ApiVersion(1)]
[Authorize]
[Route("api/v{version:apiVersion}/orders")]
public sealed class OrdersController(IOrderService orders) : ControllerBase
{
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
                    result.Status.ToString().ToSnakeCase(), result.Detail!),
            PlaceOrderStatus.TemplateUnavailable or PlaceOrderStatus.PriceUnavailable or
                PlaceOrderStatus.IdempotencyConflict =>
                ProblemResponse(409, result.Status.ToString().ToSnakeCase(), result.Detail!),
            _ => ProblemResponse(400, result.Status.ToString().ToSnakeCase(), result.Detail!)
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
