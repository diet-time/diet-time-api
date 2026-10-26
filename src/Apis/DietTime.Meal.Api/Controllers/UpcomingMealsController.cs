using System.Security.Claims;
using Asp.Versioning;
using DietTime.Application;
using DietTime.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DietTime.Meal.Api.Controllers;

[ApiController, ApiVersion(1), Authorize]
[Route("api/v{version:apiVersion}/orders/{orderId:guid}")]
public sealed class UpcomingMealsController(IUpcomingMealService meals, ILanguageResolver languages) : ControllerBase
{
    [HttpGet("tomorrow-menu")]
    public async Task<IActionResult> Tomorrow(Guid orderId, [FromQuery] string? language, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var userId)) return Unauthorized();
        var menu = await meals.GetTomorrowAsync(orderId, userId, languages.Resolve(language, Request.Headers.AcceptLanguage), ct);
        return menu is null ? NotFound() : Ok(ApiResponse<TomorrowMenuResponse>.Ok(menu));
    }

    [HttpPost("service-days/{dayId:guid}/confirm-meals")]
    public async Task<IActionResult> Confirm(Guid orderId, Guid dayId, ConfirmMealsRequest request,
        [FromQuery] string? language, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var userId)) return Unauthorized();
        try
        {
            var menu = await meals.ConfirmAsync(orderId, dayId, userId,
                languages.Resolve(language, Request.Headers.AcceptLanguage), request, ct);
            return Ok(ApiResponse<TomorrowMenuResponse>.Ok(menu));
        }
        catch (UpcomingMealException error)
        {
            var problem = new ProblemDetails { Status = error.StatusCode, Title = error.Message };
            problem.Extensions["code"] = error.Code;
            return StatusCode(error.StatusCode, problem);
        }
    }

    [HttpGet("upcoming-meals")]
    public async Task<IActionResult> Days(Guid orderId, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var userId)) return Unauthorized();
        var days = await meals.GetDaysAsync(orderId, userId, ct);
        return days is null ? NotFound() : Ok(ApiResponse<IReadOnlyList<UpcomingMealDayResponse>>.Ok(days));
    }

    [HttpGet("service-days/{dayId:guid}/menu")]
    public async Task<IActionResult> Menu(Guid orderId, Guid dayId, [FromQuery] string? language, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var userId)) return Unauthorized();
        var menu = await meals.GetMenuAsync(orderId, dayId, userId, languages.Resolve(language, Request.Headers.AcceptLanguage), ct);
        return menu is null ? NotFound() : Ok(ApiResponse<DailyMealMenuResponse>.Ok(menu));
    }

    [HttpPut("service-days/{dayId:guid}/meal-selections/{selectionId:guid}")]
    public async Task<IActionResult> Replace(Guid orderId, Guid dayId, Guid selectionId,
        ReplaceAssignedMealRequest request, [FromQuery] string? language, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var userId)) return Unauthorized();
        if (request.MealItemId == Guid.Empty || request.SlotOptionId == Guid.Empty || request.ExpectedRevision < 1)
            return BadRequest(new ProblemDetails { Title = "A meal, menu option and current revision are required.", Status = 400 });
        try
        {
            var menu = await meals.ReplaceAsync(orderId, dayId, selectionId, userId,
                languages.Resolve(language, Request.Headers.AcceptLanguage), request, ct);
            return Ok(ApiResponse<DailyMealMenuResponse>.Ok(menu));
        }
        catch (UpcomingMealException error)
        {
            var problem = new ProblemDetails { Status = error.StatusCode, Title = error.Message };
            problem.Extensions["code"] = error.Code;
            return StatusCode(error.StatusCode, problem);
        }
    }
}
