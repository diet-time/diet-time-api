using System.Security.Claims;
using DietTime.Application;
using DietTime.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DietTime.Meal.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/admin/orders/package-requests")]
public sealed class PackageChangeReviewController(IPackageManagementService packages) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(int pageNumber = 1, int pageSize = 20, CancellationToken ct = default) =>
        pageNumber < 1 || pageSize is < 1 or > 100 ? BadRequest() : Ok(await packages.ListChangeRequestsAsync(pageNumber, pageSize, ct));

    [HttpPost("{requestId:guid}/review")]
    public async Task<IActionResult> Review(Guid requestId, PackageChangeReviewRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var reviewer)) return Unauthorized();
        try { return Ok(await packages.ReviewChangeRequestAsync(requestId, reviewer, request, ct)); }
        catch (UpcomingMealException e) { return StatusCode(e.StatusCode, new ProblemDetails { Status = e.StatusCode, Title = e.Code, Detail = e.Message, Extensions = { ["code"] = e.Code } }); }
        catch (ArgumentOutOfRangeException) { return Conflict(new ProblemDetails { Detail = "The requested dates are no longer available. Reject this request and ask the customer to submit current dates." }); }
        catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException) { return Conflict(new ProblemDetails { Detail = "The package changed. Refresh the request before reviewing it." }); }
    }
}
