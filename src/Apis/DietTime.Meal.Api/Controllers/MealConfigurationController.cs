using System.Security.Claims;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Meal.Api.Controllers;

[ApiController, Authorize(Roles = "Admin,Dietitian,ContentManager"), Route("api/admin")]
public sealed class MealConfigurationController(
    IMealPackageService packages,
    IMealPlanPricingService pricing,
    IWeeklyMenuService weeklyMenus,
    DietTimeDbContext db) : ControllerBase
{
    private Guid? UserId => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    [HttpGet("package-options")]
    public Task<IReadOnlyList<MealPackageOptionResponse>> GetPackages([FromQuery] bool activeOnly = false, CancellationToken ct = default) => packages.GetAsync(activeOnly, ct);

    [HttpGet("package-options/{id:guid}")]
    public async Task<ActionResult<MealPackageOptionResponse>> GetPackage(Guid id, CancellationToken ct)
    {
        var result = await packages.GetAsync(id, ct);
        return result is null
            ? NotFound(new ApiResponse<object> { Errors = [new("package_option_not_found", "The package option does not exist.")] })
            : Ok(result);
    }

    [HttpPost("package-options")]
    public async Task<ActionResult<MealPackageOptionResponse>> CreatePackage(UpsertMealPackageOptionRequest request, CancellationToken ct)
    {
        var id = await packages.CreateAsync(request, UserId, ct);
        return StatusCode(StatusCodes.Status201Created, await packages.GetAsync(id, ct));
    }

    [HttpPut("package-options/{id:guid}")]
    public async Task<IActionResult> UpdatePackage(Guid id, UpsertMealPackageOptionRequest request, CancellationToken ct)
    {
        await packages.UpdateAsync(id, request, UserId, ct);
        return NoContent();
    }

    [HttpPatch("package-options/{id:guid}/status")]
    public async Task<IActionResult> SetPackageStatus(Guid id, SetActiveStatusRequest request, CancellationToken ct)
    {
        await packages.SetStatusAsync(id, request.IsActive, UserId, ct);
        return NoContent();
    }

    [HttpGet("package-options/{packageOptionId:guid}/meal-types")]
    public Task<IReadOnlyList<PackageMealTypeResponse>> GetPackageMealTypes(Guid packageOptionId, CancellationToken ct) => packages.GetMealTypesAsync(packageOptionId, ct);

    [HttpPut("package-options/{packageOptionId:guid}/meal-types")]
    public async Task<IActionResult> UpdatePackageMealTypes(Guid packageOptionId, UpdatePackageMealTypesRequest request, CancellationToken ct)
    {
        await packages.UpdateMealTypesAsync(packageOptionId, request, ct);
        return NoContent();
    }

    [HttpGet("meal-plan-prices")]
    public Task<IReadOnlyList<MealPlanPricingResponse>> GetPrices([FromQuery] Guid? mealPlanId, [FromQuery] Guid? durationId,
        [FromQuery] Guid? packageOptionId, [FromQuery] bool activeOnly = false, CancellationToken ct = default) =>
        pricing.GetAsync(mealPlanId, durationId, packageOptionId, activeOnly, ct);

    [HttpPost("meal-plan-prices")]
    public async Task<IActionResult> CreatePrice(UpsertMealPlanPricingRequest request, CancellationToken ct)
    {
        var id = await pricing.CreateAsync(request, UserId, ct);
        return StatusCode(StatusCodes.Status201Created, new { id });
    }

    [HttpPut("meal-plan-prices/{id:guid}")]
    public async Task<IActionResult> UpdatePrice(Guid id, UpsertMealPlanPricingRequest request, CancellationToken ct)
    {
        await pricing.UpdateAsync(id, request, UserId, ct);
        return NoContent();
    }

    [HttpGet("meal-plans/{mealPlanId:guid}/weekly-menu")]
    public Task<WeeklyMenuResponse> GetWeeklyMenu(Guid mealPlanId, CancellationToken ct) => weeklyMenus.GetAsync(mealPlanId, ct);

    [HttpGet("meal-plans/{mealPlanId:guid}/weekly-menu/{dayOfWeek:int}")]
    public Task<WeeklyMenuDayResponse> GetWeeklyMenuDay(Guid mealPlanId, int dayOfWeek, CancellationToken ct) => weeklyMenus.GetDayAsync(mealPlanId, dayOfWeek, ct);

    [HttpPut("meal-plans/{mealPlanId:guid}/weekly-menu/{dayOfWeek:int}")]
    public async Task<IActionResult> UpdateWeeklyMenuDay(Guid mealPlanId, int dayOfWeek, UpdateWeeklyMenuDayRequest request, CancellationToken ct)
    {
        await weeklyMenus.UpdateDayAsync(mealPlanId, dayOfWeek, request, UserId, ct);
        return NoContent();
    }

    [HttpGet("meal-types")]
    public async Task<IReadOnlyList<AdminMealTypeLookupResponse>> GetMealTypes(CancellationToken ct) =>
        await db.MealTypes.AsNoTracking().OrderBy(x => x.DisplayOrder).ThenBy(x => x.Code)
            .Select(x => new AdminMealTypeLookupResponse(x.Id, x.Code, x.DisplayOrder, x.IsActive)).ToListAsync(ct);
}
