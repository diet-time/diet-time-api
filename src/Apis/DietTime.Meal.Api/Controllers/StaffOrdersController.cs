using System.Security.Claims;
using DietTime.Contracts;
using DietTime.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DietTime.Meal.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/admin/orders")]
public sealed class StaffOrdersController(StaffOrderService orders) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(string? status = null, int pageNumber = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1 || pageSize is < 1 or > 100 || status?.Length > 30) return BadRequest();
        return Ok(await orders.ListAsync(status?.Trim().ToUpperInvariant(), pageNumber, pageSize, cancellationToken));
    }

    [HttpGet("{orderId:guid}")]
    public async Task<IActionResult> Get(Guid orderId, CancellationToken ct) => await orders.GetAsync(orderId, ct) is { } order ? Ok(order) : NotFound();

    [HttpPost("{orderId:guid}/approve")]
    public Task<IActionResult> Approve(Guid orderId, OrderReviewRequest r, CancellationToken ct) => Change(orderId, "approve", r, ct);
    [HttpPost("{orderId:guid}/reject")]
    public Task<IActionResult> Reject(Guid orderId, OrderReviewRequest r, CancellationToken ct) => Change(orderId, "reject", r, ct);
    [HttpPost("{orderId:guid}/hold")]
    public Task<IActionResult> Hold(Guid orderId, OrderReviewRequest r, CancellationToken ct) => Change(orderId, "hold", r, ct);
    [HttpPost("{orderId:guid}/resume")]
    public Task<IActionResult> Resume(Guid orderId, OrderReviewRequest r, CancellationToken ct) => Change(orderId, "resume", r, ct);
    [HttpPost("{orderId:guid}/cancel")]
    public Task<IActionResult> Cancel(Guid orderId, OrderReviewRequest r, CancellationToken ct) => Change(orderId, "cancel", r, ct);
    [HttpPost("{orderId:guid}/activate")]
    public Task<IActionResult> Activate(Guid orderId, OrderReviewRequest r, CancellationToken ct) => Change(orderId, "activate", r, ct);
    [HttpPost("{orderId:guid}/complete")]
    public Task<IActionResult> Complete(Guid orderId, OrderReviewRequest r, CancellationToken ct) => Change(orderId, "complete", r, ct);
    [HttpPost("{orderId:guid}/payment")]
    public Task<IActionResult> Payment(Guid orderId, RecordOrderPaymentRequest r, CancellationToken ct) => Change(orderId, "payment", new(r.ExpectedRowVersion, r.Reason), ct, r);
    [HttpPost("{orderId:guid}/revise-dates")]
    public Task<IActionResult> Dates(Guid orderId, ReviseOrderDatesRequest r, CancellationToken ct) => Change(orderId, "revise-dates", new(r.ExpectedRowVersion, r.Reason), ct, dates: r);

    private async Task<IActionResult> Change(Guid id, string action, OrderReviewRequest r, CancellationToken ct,
        RecordOrderPaymentRequest? payment = null, ReviseOrderDatesRequest? dates = null)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var user)) return Unauthorized();
        try { return Ok(await orders.ChangeAsync(id, action, r.ExpectedRowVersion, r.Reason, user, ct, payment, dates)); }
        catch (OrderReviewException error) { return StatusCode(error.Status, new ProblemDetails { Status = error.Status, Title = error.Code, Detail = error.Message, Extensions = { ["code"] = error.Code } }); }
    }
}
