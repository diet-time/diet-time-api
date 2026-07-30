using Asp.Versioning;
using DietTime.Application;
using DietTime.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DietTime.Meal.Api.Controllers;

[ApiController, ApiVersion(1), Authorize, Route("api/v{version:apiVersion}/meal-selections")]
public sealed class MealSelectionsController(IMealSelectionService selections) : ControllerBase
{
    [HttpPost("validate")] public async Task<ActionResult<ApiResponse<MealSelectionValidationResponse>>> Validate(MealSelectionRequest request, CancellationToken ct) => Ok(ApiResponse<MealSelectionValidationResponse>.Ok(await selections.ValidateAsync(request, DateTimeOffset.UtcNow, ct)));
}
