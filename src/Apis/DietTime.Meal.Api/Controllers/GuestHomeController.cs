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
    /// Returns localized active plans with the selected plan's slots and meals nested beneath it,
    /// plus the seven-day menu calendar, meal-time filters, and pagination.
    ///
    /// Example: `GET /api/v1/guest/home?language=en&amp;date=2026-07-24&amp;planCode=CLASSIC&amp;mealTimeCode=ALL&amp;page=1&amp;pageSize=20`
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
