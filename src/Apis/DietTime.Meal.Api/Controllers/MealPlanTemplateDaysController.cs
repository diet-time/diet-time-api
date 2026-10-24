using System.Security.Claims;
using Asp.Versioning;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DietTime.Meal.Api.Controllers;

[ApiController]
[ApiVersion(1)]
[Authorize(Roles = "Admin,Dietitian,ContentManager")]
[Route("api/meal-plan-templates/{templateId:guid}/days")]
[Route("api/v{version:apiVersion}/admin/meal-plan-templates/{templateId:guid}/days")]
public sealed class MealPlanTemplateDaysController(IAdminMealService mealPlans) : ControllerBase
{
    private Guid? UserId => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    /// <summary>Returns the template's weekday menus ordered by display order.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<MealPlanTemplateDayResponse>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<MealPlanTemplateDayResponse>>>> Get(Guid templateId, [FromQuery] int? weekNumber, CancellationToken ct)
    {
        if (weekNumber is < 1 or > 4) return BadRequest(new TemplateDayErrorResponse("INVALID_WEEK_NUMBER", "weekNumber must be between 1 and 4."));
        var days = await mealPlans.GetTemplateDaysAsync(templateId, weekNumber, ct);
        return days is null ? NotFound() : Ok(ApiResponse<IReadOnlyList<MealPlanTemplateDayResponse>>.Ok(days));
    }

    /// <summary>Adds one weekday menu to a template.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(TemplateDayErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<object>>> Create(Guid templateId, UpsertMealPlanTemplateDayRequest request, CancellationToken ct)
    {
        var id = await mealPlans.CreateTemplateDayAsync(templateId, request, UserId, ct);
        return id is null
            ? NotFound()
            : StatusCode(StatusCodes.Status201Created, ApiResponse<object>.Ok(new { id }));
    }

    /// <summary>Updates a weekday menu's weekday, display order, and active state.</summary>
    [HttpPut("{dayId:guid}")]
    public async Task<IActionResult> Update(Guid templateId, Guid dayId, UpsertMealPlanTemplateDayRequest request, CancellationToken ct) =>
        await mealPlans.UpdateTemplateDayAsync(templateId, dayId, request, UserId, ct) ? NoContent() : NotFound();

    /// <summary>Soft-deactivates a weekday menu without deleting its slots or options.</summary>
    [HttpDelete("{dayId:guid}")]
    public async Task<IActionResult> Delete(Guid templateId, Guid dayId, CancellationToken ct) =>
        await mealPlans.DeactivateTemplateDayAsync(templateId, dayId, UserId, ct) ? NoContent() : NotFound();

    /// <summary>Returns the active menu, slots, options, and selection rules for a weekday.</summary>
    [HttpGet("by-weekday/{weekday}")]
    [ProducesResponseType(typeof(ApiResponse<MealPlanTemplateDayDetailResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(TemplateDayErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<MealPlanTemplateDayDetailResponse>>> ByWeekday(Guid templateId, string weekday, [FromQuery] int? weekNumber, CancellationToken ct)
    {
        if (weekNumber is < 1 or > 4) return BadRequest(new TemplateDayErrorResponse("INVALID_WEEK_NUMBER", "weekNumber must be between 1 and 4."));
        if (!Enum.TryParse<MenuWeekday>(weekday, true, out var parsed))
            return BadRequest(new TemplateDayErrorResponse("UNSUPPORTED_WEEKDAY", $"'{weekday}' is not a supported menu weekday."));
        var day = await mealPlans.GetTemplateDayByWeekdayAsync(templateId, parsed, weekNumber, ct);
        return day is null
            ? NotFound(new TemplateDayErrorResponse("MENU_NOT_CONFIGURED_FOR_WEEKDAY", $"No {parsed} menu is configured for this template."))
            : Ok(ApiResponse<MealPlanTemplateDayDetailResponse>.Ok(day));
    }

    [HttpPost("~/api/meal-plan-templates/{templateId:guid}/weeks/copy")]
    [HttpPost("~/api/v{version:apiVersion}/admin/meal-plan-templates/{templateId:guid}/weeks/copy")]
    public async Task<IActionResult> CopyWeek(Guid templateId, CopyMenuWeekRequest request, CancellationToken ct)
    {
        if (request.SourceWeekNumber is < 1 or > 4 || request.TargetWeekNumber is < 1 or > 4 || request.SourceWeekNumber == request.TargetWeekNumber)
            return BadRequest(new TemplateDayErrorResponse("INVALID_WEEK_COPY", "Source and target weeks must be different week numbers between 1 and 4."));
        return await mealPlans.CopyTemplateWeekAsync(templateId, request, UserId, ct) ? NoContent() : NotFound();
    }

    [HttpGet("~/api/meal-plan-templates/{templateId:guid}/validation")]
    [HttpGet("~/api/v{version:apiVersion}/admin/meal-plan-templates/{templateId:guid}/validation")]
    public async Task<ActionResult<ApiResponse<MenuValidationResponse>>> Validate(Guid templateId, CancellationToken ct)
    {
        var result = await mealPlans.ValidateMealPlanMenuAsync(templateId, ct);
        return result is null ? NotFound() : Ok(ApiResponse<MenuValidationResponse>.Ok(result));
    }
}
