using Asp.Versioning;
using DietTime.Application;
using DietTime.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DietTime.Meal.Api.Controllers;

[ApiController]
[ApiVersion(1)]
[AllowAnonymous]
[Route("api/v{version:apiVersion}/guest")]
public sealed class GuestHomeController(
    IGuestHomeService guestHome,
    TimeProvider clock) : ControllerBase
{
    /// <summary>Gets the complete guest home screen.</summary>
    /// <remarks>
    /// Returns localized active plans with every configured Saturday-Friday menu nested beneath
    /// each plan. The selected date's slots are also returned directly for backward compatibility,
    /// together with the weekly calendar, meal-time filters, and pagination.
    ///
    /// The top-level `menus` collection contains the same plan/date menu groups for compatibility
    /// with clients that perform their filtering outside the nested meal-plan structure.
    ///
    /// Example: `GET /api/v1/guest/home?language=en`
    /// </remarks>
    /// <response code="200">The complete guest home payload.</response>
    /// <response code="400">A query parameter is invalid or the requested plan is not active.</response>
    /// <response code="404">No active menu exists for the selected plan and date.</response>
    /// <response code="500">The request could not be completed.</response>
    [HttpGet("home")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(ApiResponse<GuestHomeResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ApiResponse<GuestHomeResponse>>> Get(
        [FromQuery] GuestHomeQuery query,
        CancellationToken ct)
    {
        var response = await guestHome.GetAsync(query, clock.GetUtcNow(), ct);
        return response is null
            ? NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Menu not found",
                Detail = "No active menu exists for the selected plan and date."
            })
            : Ok(ApiResponse<GuestHomeResponse>.Ok(response));
    }
}
