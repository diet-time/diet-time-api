using System.Security.Claims;
using Asp.Versioning;
using DietTime.Application;
using DietTime.Contracts;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DietTime.Meal.Api.Controllers;

[ApiController, ApiVersion(1), Authorize(Roles = "Admin,Dietitian,ContentManager"), Route("api/v{version:apiVersion}/admin")]
public sealed class AdminController(IAdminMealService admin, IStorageUrlService storage, IValidator<UploadUrlRequest> uploadValidator) : ControllerBase
{
    private Guid? UserId => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    [HttpGet("dashboard")] public async Task<ActionResult<AdminDashboardResponse>> Dashboard(CancellationToken ct) => Ok(await admin.GetDashboardAsync(ct));
    [HttpGet("allergens")] public async Task<ActionResult<ApiResponse<IReadOnlyList<AdminAllergenResponse>>>> Allergens([FromQuery] string? search, [FromQuery] string? sort, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        if (page < 1 || pageSize is < 1 or > 100) return BadRequest();
        var result = await admin.GetAllergensAsync(search, sort, page, pageSize, ct);
        return Ok(ApiResponse<IReadOnlyList<AdminAllergenResponse>>.Ok(result.Items, result.Meta));
    }
    [HttpPost("allergens")] public async Task<ActionResult<ApiResponse<object>>> CreateAllergen(UpsertAllergenRequest request, CancellationToken ct)
    {
        var id = await admin.CreateAllergenAsync(request, UserId, ct);
        return id is null
            ? Conflict(new ApiResponse<object> { Errors = [new("duplicate_code", "An allergen with this code already exists.", "code")] })
            : StatusCode(StatusCodes.Status201Created, ApiResponse<object>.Ok(new { id }));
    }
    [HttpPut("allergens/{allergenId:guid}")] public async Task<IActionResult> UpdateAllergen(Guid allergenId, UpsertAllergenRequest request, CancellationToken ct)
    {
        return await admin.UpdateAllergenAsync(allergenId, request, UserId, ct) switch
        {
            AdminWriteResult.Success => NoContent(),
            AdminWriteResult.NotFound => NotFound(),
            _ => Conflict(new ApiResponse<object> { Errors = [new("duplicate_code", "An allergen with this code already exists.", "code")] })
        };
    }
    [HttpGet("ingredients")] public async Task<ActionResult<ApiResponse<IReadOnlyList<AdminIngredientResponse>>>> Ingredients([FromQuery] string? search, [FromQuery] string? sort, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        if (page < 1 || pageSize is < 1 or > 100) return BadRequest();
        var result = await admin.GetIngredientsAsync(search, sort, page, pageSize, ct);
        return Ok(ApiResponse<IReadOnlyList<AdminIngredientResponse>>.Ok(result.Items, result.Meta));
    }
    [HttpPost("ingredients")] public async Task<ActionResult<ApiResponse<object>>> CreateIngredient(UpsertIngredientRequest request, CancellationToken ct)
    {
        var id = await admin.CreateIngredientAsync(request, UserId, ct);
        return id is null
            ? Conflict(new ApiResponse<object> { Errors = [new("duplicate_code", "An ingredient with this code already exists.", "code")] })
            : StatusCode(StatusCodes.Status201Created, ApiResponse<object>.Ok(new { id }));
    }
    [HttpPut("ingredients/{ingredientId:guid}")] public async Task<IActionResult> UpdateIngredient(Guid ingredientId, UpsertIngredientRequest request, CancellationToken ct)
    {
        return await admin.UpdateIngredientAsync(ingredientId, request, UserId, ct) switch
        {
            AdminWriteResult.Success => NoContent(),
            AdminWriteResult.NotFound => NotFound(),
            _ => Conflict(new ApiResponse<object> { Errors = [new("duplicate_code", "An ingredient with this code already exists.", "code")] })
        };
    }
    [HttpGet("meal-categories")] public async Task<ActionResult<ApiResponse<IReadOnlyList<AdminMealCategoryResponse>>>> MealCategories([FromQuery] string? search, [FromQuery] string? sort, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        if (page < 1 || pageSize is < 1 or > 100) return BadRequest();
        var result = await admin.GetMealCategoriesAsync(search, sort, page, pageSize, ct);
        return Ok(ApiResponse<IReadOnlyList<AdminMealCategoryResponse>>.Ok(result.Items, result.Meta));
    }
    [HttpPost("meal-categories")] public async Task<ActionResult<ApiResponse<object>>> CreateMealCategory(UpsertMealCategoryRequest request, CancellationToken ct)
    {
        var id = await admin.CreateMealCategoryAsync(request, UserId, ct);
        return id is null
            ? Conflict(new ApiResponse<object> { Errors = [new("duplicate_code", "A meal category with this code already exists.", "code")] })
            : StatusCode(StatusCodes.Status201Created, ApiResponse<object>.Ok(new { id }));
    }
    [HttpPut("meal-categories/{categoryId:guid}")] public async Task<IActionResult> UpdateMealCategory(Guid categoryId, UpsertMealCategoryRequest request, CancellationToken ct)
    {
        return await admin.UpdateMealCategoryAsync(categoryId, request, UserId, ct) switch
        {
            AdminWriteResult.Success => NoContent(),
            AdminWriteResult.NotFound => NotFound(),
            _ => Conflict(new ApiResponse<object> { Errors = [new("duplicate_code", "A meal category with this code already exists.", "code")] })
        };
    }
    [HttpPost("meals")] public async Task<ActionResult<ApiResponse<object>>> CreateMeal(UpsertMealRequest request, CancellationToken ct) { var id = await admin.CreateMealAsync(request, UserId, ct); return CreatedAtAction(nameof(GetMeal), new { version = "1", mealId = id }, ApiResponse<object>.Ok(new { id })); }
    [HttpPut("meals/{mealId:guid}")] public async Task<IActionResult> UpdateMeal(Guid mealId, UpsertMealRequest request, CancellationToken ct) => await admin.UpdateMealAsync(mealId, request, UserId, ct) ? NoContent() : NotFound();
    [HttpGet("meals")] public async Task<ActionResult<ApiResponse<IReadOnlyList<AdminMealSummaryResponse>>>> GetMeals([FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) { if (page < 1 || pageSize is < 1 or > 100) return BadRequest(); var rows = await admin.GetMealsAsync(search, page, pageSize, ct); return Ok(ApiResponse<IReadOnlyList<AdminMealSummaryResponse>>.Ok(rows.Items, rows.Meta)); }
    [HttpGet("meals/{mealId:guid}")] public async Task<ActionResult<ApiResponse<AdminMealResponse>>> GetMeal(Guid mealId, CancellationToken ct) { var meal = await admin.GetMealAsync(mealId, ct); return meal is null ? NotFound() : Ok(ApiResponse<AdminMealResponse>.Ok(meal)); }
    [HttpPatch("meals/{mealId:guid}/status")] public async Task<IActionResult> Status(Guid mealId, ChangeMealStatusRequest request, CancellationToken ct) => await admin.SetMealStatusAsync(mealId, request.Status.ToUpperInvariant(), UserId, ct) ? NoContent() : NotFound();
    [HttpPost("meals/{mealId:guid}/media")] public async Task<ActionResult<ApiResponse<object>>> Media(Guid mealId, SaveMediaRequest request, CancellationToken ct) { var id = await admin.AddMediaAsync(mealId, request, UserId, ct); return id is null ? NotFound() : Ok(ApiResponse<object>.Ok(new { id })); }
    [HttpDelete("meals/{mealId:guid}/media/{mediaId:guid}")] public async Task<IActionResult> DeleteMedia(Guid mealId, Guid mediaId, CancellationToken ct) => await admin.DeleteMediaAsync(mealId, mediaId, ct) ? NoContent() : NotFound();
    [HttpPost("media/upload-url")] public async Task<ActionResult<ApiResponse<UploadUrlResponse>>> Upload(UploadUrlRequest request, CancellationToken ct) { await uploadValidator.ValidateAndThrowAsync(request, ct); return Ok(ApiResponse<UploadUrlResponse>.Ok(await storage.CreateUploadUrlAsync(request, ct))); }
    [HttpPost("meal-plans")] public async Task<ActionResult<ApiResponse<object>>> CreatePlan(CreatePlanRequest request, CancellationToken ct) { var id = await admin.CreatePlanAsync(request, UserId, ct); return Ok(ApiResponse<object>.Ok(new { id })); }
    [HttpPut("meal-plans/{planId:guid}")] public async Task<IActionResult> UpdatePlan(Guid planId, CreatePlanRequest request, CancellationToken ct) => await admin.UpdatePlanAsync(planId, request, UserId, ct) ? NoContent() : NotFound();
    [HttpPost("meal-plans/{planId:guid}/days")] public async Task<ActionResult<ApiResponse<object>>> Day(Guid planId, CreatePlanDayRequest request, CancellationToken ct) { var id = await admin.AddPlanDayAsync(planId, request, UserId, ct); return id is null ? NotFound() : Ok(ApiResponse<object>.Ok(new { id })); }
    [HttpPost("meal-plan-days/{dayId:guid}/slots")] public async Task<ActionResult<ApiResponse<object>>> Slot(Guid dayId, CreatePlanSlotRequest request, CancellationToken ct) { var id = await admin.AddPlanSlotAsync(dayId, request, UserId, ct); return id is null ? NotFound() : Ok(ApiResponse<object>.Ok(new { id })); }
    [HttpPost("meal-plan-slots/{slotId:guid}/options")] public async Task<ActionResult<ApiResponse<object>>> Option(Guid slotId, CreateSlotOptionRequest request, CancellationToken ct) { var id = await admin.AddSlotOptionAsync(slotId, request, UserId, ct); return id is null ? NotFound() : Ok(ApiResponse<object>.Ok(new { id })); }
    [HttpDelete("meal-plan-slots/{slotId:guid}/options/{optionId:guid}")] public async Task<IActionResult> DeleteOption(Guid slotId, Guid optionId, CancellationToken ct) => await admin.DeleteSlotOptionAsync(slotId, optionId, ct) ? NoContent() : NotFound();
    [HttpPost("meal-plans/{planId:guid}/publish")] public async Task<IActionResult> Publish(Guid planId, CancellationToken ct) => await admin.SetPlanPublishedAsync(planId, true, UserId, ct) ? NoContent() : NotFound();
    [HttpPost("meal-plans/{planId:guid}/unpublish")] public async Task<IActionResult> Unpublish(Guid planId, CancellationToken ct) => await admin.SetPlanPublishedAsync(planId, false, UserId, ct) ? NoContent() : NotFound();
}
