using Asp.Versioning;
using DietTime.Application;
using DietTime.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;

namespace DietTime.Meal.Api.Controllers;

[ApiController]
[ApiVersion(1)]
[AllowAnonymous]
[Route("api/v{version:apiVersion}/guest")]
public sealed class GuestHomeController(
    IGuestHomeService guestHome,
    TimeProvider clock) : ControllerBase
{
    private static readonly Regex PlanCodePattern = new(
        "^[a-zA-Z0-9_-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Gets lightweight guest home data.</summary>
    /// <remarks>
    /// Returns localized active plans, slot definitions, and the selected plan's weekly calendar.
    /// Meal choices are returned by the dedicated plan menu endpoint.
    ///
    /// Example: `GET /api/v1/guest/home?language=en`
    /// </remarks>
    /// <response code="200">The lightweight guest home payload.</response>
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

    /// <summary>Gets the menu for one meal plan and date.</summary>
    /// <remarks>
    /// Example: `GET /api/v1/guest/meal-plans/CLASSIC/menu?date=2026-07-23&amp;language=en`
    /// </remarks>
    /// <response code="200">The plan/date menu with slots and available meals.</response>
    /// <response code="400">The plan code or query is invalid.</response>
    /// <response code="404">No active menu exists for the plan and date.</response>
    [HttpGet("meal-plans/{planCode}/menu")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(ApiResponse<GuestMenuResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<GuestMenuResponse>>> GetMenu(
        string planCode,
        [FromQuery] GuestMenuQuery query,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(planCode) ||
            planCode.Length > 100 ||
            !PlanCodePattern.IsMatch(planCode))
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid plan code"
            });
        }

        var response = await guestHome.GetMenuAsync(planCode, query, clock.GetUtcNow(), ct);
        return response is null
            ? NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Menu not found",
                Detail = "No active menu exists for the selected plan and date."
            })
            : Ok(ApiResponse<GuestMenuResponse>.Ok(response));
    }
}
