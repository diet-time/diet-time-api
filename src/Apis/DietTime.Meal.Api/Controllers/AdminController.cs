using System.Security.Claims;
using Asp.Versioning;
using DietTime.Application;
using DietTime.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DietTime.Meal.Api.Controllers;

[ApiController, ApiVersion(1), Authorize(Roles = "Admin,Dietitian,ContentManager"), Route("api/v{version:apiVersion}/admin")]
public sealed class AdminController(IAdminMealService admin, IStorageUrlService storage, ILogger<AdminController> logger) : ControllerBase
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
    [HttpPut("meal-types/{mealTypeId:guid}")] public async Task<IActionResult> UpdateMealType(Guid mealTypeId, UpsertMealTypeRequest request, CancellationToken ct)
    {
        return await admin.UpdateMealTypeAsync(mealTypeId, request, UserId, ct) switch
        {
            AdminWriteResult.Success => NoContent(),
            AdminWriteResult.NotFound => NotFound(),
            _ => Conflict(new ApiResponse<object> { Errors = [new("duplicate_code", "A meal type with this code already exists.", "code")] })
        };
    }
    [HttpPost("meals")] public async Task<ActionResult<ApiResponse<object>>> CreateMeal(UpsertMealRequest request, CancellationToken ct) { var id = await admin.CreateMealAsync(request, UserId, ct); return CreatedAtAction(nameof(GetMeal), new { version = "1", mealId = id }, ApiResponse<object>.Ok(new { id })); }
    [HttpPut("meals/{mealId:guid}")] public async Task<IActionResult> UpdateMeal(Guid mealId, UpsertMealRequest request, CancellationToken ct) => await admin.UpdateMealAsync(mealId, request, UserId, ct) ? NoContent() : NotFound();
    [HttpGet("meals")] public async Task<ActionResult<ApiResponse<IReadOnlyList<AdminMealSummaryResponse>>>> GetMeals([FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) { if (page < 1 || pageSize is < 1 or > 100) return BadRequest(); var rows = await admin.GetMealsAsync(search, page, pageSize, ct); return Ok(ApiResponse<IReadOnlyList<AdminMealSummaryResponse>>.Ok(rows.Items, rows.Meta)); }
    [HttpGet("meals/{mealId:guid}")] public async Task<ActionResult<ApiResponse<AdminMealResponse>>> GetMeal(Guid mealId, CancellationToken ct) { var meal = await admin.GetMealAsync(mealId, ct); return meal is null ? NotFound() : Ok(ApiResponse<AdminMealResponse>.Ok(meal)); }
    [HttpPatch("meals/{mealId:guid}/status")] public async Task<IActionResult> Status(Guid mealId, ChangeMealStatusRequest request, CancellationToken ct) => await admin.SetMealStatusAsync(mealId, request.Status.ToUpperInvariant(), UserId, ct) ? NoContent() : NotFound();
    [HttpPost("meals/{mealId:guid}/media/upload"), Consumes("multipart/form-data")]
    public async Task<ActionResult<ApiResponse<AdminMediaResponse>>> UploadMealMedia(
        Guid mealId,
        [FromForm] IFormFile file,
        [FromForm] string? mediaType = "IMAGE",
        [FromForm] string? altTextEn = null,
        [FromForm] bool isPrimary = false,
        [FromForm] int displayOrder = 0,
        CancellationToken ct = default)
    {
        if (file.Length == 0)
            return BadRequest(new ProblemDetails { Title = "Invalid image", Detail = "The uploaded file is empty." });
        if (file.Length > storage.MaxUploadSizeBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new ProblemDetails { Title = "Image too large", Detail = $"The maximum upload size is {storage.MaxUploadSizeBytes} bytes." });
        if (displayOrder < 0)
            return BadRequest(new ProblemDetails { Title = "Invalid display order", Detail = "displayOrder must be zero or greater." });
        if (altTextEn?.Length > 500)
            return BadRequest(new ProblemDetails { Title = "Invalid alt text", Detail = "altTextEn cannot exceed 500 characters." });

        var normalizedMediaType = mediaType?.Trim().ToUpperInvariant() ?? "IMAGE";
        if (normalizedMediaType != "IMAGE")
            return BadRequest(new ProblemDetails { Title = "Invalid media type", Detail = "Only IMAGE media is supported by this endpoint." });

        await using var content = file.OpenReadStream();
        var detected = await DetectImageTypeAsync(content, ct);
        if (detected is null || !detected.Value.ContentType.Equals(file.ContentType, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new ProblemDetails { Title = "Invalid image", Detail = "Only valid JPEG, PNG, and WebP files are accepted, and the file content must match its Content-Type." });

        var objectKey = $"meals/{mealId:D}/{Guid.NewGuid():N}{detected.Value.Extension}";
        var uploaded = false;
        try
        {
            await storage.UploadAsync(objectKey, content, detected.Value.ContentType, ct);
            uploaded = true;

            var media = await admin.AddMediaAsync(mealId, new(
                objectKey,
                storage.GetPublicUrl(objectKey),
                detected.Value.ContentType,
                normalizedMediaType,
                isPrimary,
                displayOrder,
                altTextEn), UserId, ct);
            if (media is null)
            {
                await TryDeleteObjectAsync(objectKey);
                uploaded = false;
                return NotFound();
            }

            return StatusCode(StatusCodes.Status201Created, ApiResponse<AdminMediaResponse>.Ok(media));
        }
        catch
        {
            if (uploaded) await TryDeleteObjectAsync(objectKey);
            throw;
        }
    }
    [HttpDelete("meals/{mealId:guid}/media/{mediaId:guid}")] public async Task<IActionResult> DeleteMedia(Guid mealId, Guid mediaId, CancellationToken ct) => await admin.DeleteMediaAsync(mealId, mediaId, ct) ? NoContent() : NotFound();
    [HttpGet("meal-plans")] public async Task<ActionResult<ApiResponse<IReadOnlyList<AdminMealPlanSummaryResponse>>>> GetMealPlans([FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) { if (page < 1 || pageSize is < 1 or > 100) return BadRequest(); var rows = await admin.GetMealPlansAsync(page, pageSize, ct); return Ok(ApiResponse<IReadOnlyList<AdminMealPlanSummaryResponse>>.Ok(rows.Items, rows.Meta)); }
    [HttpPost("meal-plans")] public async Task<ActionResult<ApiResponse<object>>> CreatePlan(CreatePlanRequest request, CancellationToken ct) { var id = await admin.CreatePlanAsync(request, UserId, ct); return Ok(ApiResponse<object>.Ok(new { id })); }
    [HttpPut("meal-plans/{planId:guid}")] public async Task<IActionResult> UpdatePlan(Guid planId, CreatePlanRequest request, CancellationToken ct) => await admin.UpdatePlanAsync(planId, request, UserId, ct) ? NoContent() : NotFound();
    [HttpPost("meal-plans/{planId:guid}/days")] public async Task<ActionResult<ApiResponse<object>>> Day(Guid planId, CreatePlanDayRequest request, CancellationToken ct) { var id = await admin.AddPlanDayAsync(planId, request, UserId, ct); return id is null ? NotFound() : Ok(ApiResponse<object>.Ok(new { id })); }
    [HttpPost("meal-plan-days/{dayId:guid}/slots")] public async Task<ActionResult<ApiResponse<object>>> Slot(Guid dayId, CreatePlanSlotRequest request, CancellationToken ct) { var id = await admin.AddPlanSlotAsync(dayId, request, UserId, ct); return id is null ? NotFound() : Ok(ApiResponse<object>.Ok(new { id })); }
    [HttpPost("meal-plan-slots/{slotId:guid}/options")] public async Task<ActionResult<ApiResponse<object>>> Option(Guid slotId, CreateSlotOptionRequest request, CancellationToken ct) { var id = await admin.AddSlotOptionAsync(slotId, request, UserId, ct); return id is null ? NotFound() : Ok(ApiResponse<object>.Ok(new { id })); }
    [HttpDelete("meal-plan-slots/{slotId:guid}/options/{optionId:guid}")] public async Task<IActionResult> DeleteOption(Guid slotId, Guid optionId, CancellationToken ct) => await admin.DeleteSlotOptionAsync(slotId, optionId, ct) ? NoContent() : NotFound();
    [HttpPost("meal-plans/{planId:guid}/publish")] public async Task<IActionResult> Publish(Guid planId, CancellationToken ct) => await admin.SetPlanPublishedAsync(planId, true, UserId, ct) ? NoContent() : NotFound();
    [HttpPost("meal-plans/{planId:guid}/unpublish")] public async Task<IActionResult> Unpublish(Guid planId, CancellationToken ct) => await admin.SetPlanPublishedAsync(planId, false, UserId, ct) ? NoContent() : NotFound();

    private async Task TryDeleteObjectAsync(string objectKey)
    {
        try { await storage.DeleteAsync(objectKey, CancellationToken.None); }
        catch (Exception ex) { logger.LogError(ex, "Failed to delete orphaned storage object {ObjectKey}", objectKey); }
    }

    private static async Task<(string ContentType, string Extension)?> DetectImageTypeAsync(Stream content, CancellationToken ct)
    {
        var header = new byte[12];
        var read = 0;
        while (read < header.Length)
        {
            var count = await content.ReadAsync(header.AsMemory(read, header.Length - read), ct);
            if (count == 0) break;
            read += count;
        }
        if (content.CanSeek) content.Position = 0;

        if (read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            return ("image/jpeg", ".jpg");
        if (read >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return ("image/png", ".png");
        if (read >= 12 && header.AsSpan(0, 4).SequenceEqual("RIFF"u8) && header.AsSpan(8, 4).SequenceEqual("WEBP"u8))
            return ("image/webp", ".webp");
        return null;
    }
}
