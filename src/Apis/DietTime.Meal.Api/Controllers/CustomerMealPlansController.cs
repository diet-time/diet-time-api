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
[Route("api/v{version:apiVersion}/customer/meal-plans")]
public sealed class CustomerMealPlansController(
    ICustomerMealPlanPurchaseService purchases,
    ILanguageResolver languages) : ControllerBase
{
    [HttpGet("{mealPlanCode}/purchase-options")]
    [ProducesResponseType(typeof(ApiResponse<MealPlanPurchaseOptionsResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetPurchaseOptions(
        string mealPlanCode,
        [FromQuery] string? language,
        CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var resolvedLanguage = languages.Resolve(language, Request.Headers.AcceptLanguage);
        var options = await purchases.GetPurchaseOptionsAsync(
            mealPlanCode,
            userId,
            resolvedLanguage,
            ct);

        return options is null
            ? NotFound(Error("PLAN_NOT_FOUND", "The selected meal plan is not available."))
            : Ok(ApiResponse<MealPlanPurchaseOptionsResponse>.Ok(options));
    }

    [HttpPost("validate-selection")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(ApiResponse<MealPlanSelectionValidationResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ValidateSelection(
        ValidateMealPlanSelectionRequest request,
        CancellationToken ct)
    {
        if (!TryGetUserId(out _))
            return Unauthorized();

        var result = await purchases.ValidateSelectionAsync(request, ct);
        return result.Status switch
        {
            MealPlanSelectionValidationStatus.Valid =>
                Ok(ApiResponse<MealPlanSelectionValidationResponse>.Ok(result.Selection!)),
            MealPlanSelectionValidationStatus.PriceNotFound =>
                NotFound(Error("PRICE_NOT_FOUND", "The selected price does not exist.")),
            MealPlanSelectionValidationStatus.WrongPlan =>
                BadRequest(Error("PRICE_PLAN_MISMATCH", "The selected price does not belong to the meal plan.")),
            MealPlanSelectionValidationStatus.PriceInactive =>
                UnprocessableEntity(Error("PRICE_INACTIVE", "The selected price is inactive.")),
            MealPlanSelectionValidationStatus.PriceNotEffective =>
                UnprocessableEntity(Error("PRICE_NOT_EFFECTIVE", "The selected price is not effective yet.")),
            MealPlanSelectionValidationStatus.PriceExpired =>
                UnprocessableEntity(Error("PRICE_EXPIRED", "The selected price has expired.")),
            MealPlanSelectionValidationStatus.PricePackageNotFound =>
                UnprocessableEntity(Error("PRICE_PACKAGE_NOT_FOUND", "No package matches the selected price duration.")),
            MealPlanSelectionValidationStatus.PricePackageInactive =>
                UnprocessableEntity(Error("PRICE_PACKAGE_INACTIVE", "The selected price package is inactive.")),
            _ => throw new InvalidOperationException("Unsupported meal-plan selection validation status.")
        };
    }

    private static ApiResponse<object> Error(string code, string message) =>
        new() { Errors = [new ApiError(code, message)] };

    private bool TryGetUserId(out Guid userId)
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? User.FindFirstValue("sub");
        return Guid.TryParse(value, out userId);
    }
}
