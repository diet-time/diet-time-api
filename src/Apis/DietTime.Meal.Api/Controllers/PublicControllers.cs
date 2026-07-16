using Asp.Versioning;
using DietTime.Application;
using DietTime.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace DietTime.Meal.Api.Controllers;

[ApiController, ApiVersion(1), Route("api/v{version:apiVersion}")]
public sealed class CatalogueController(IMealQueryService queries, ILanguageResolver languages, TimeProvider clock) : ControllerBase
{
    private string Language() => languages.Resolve(Request.Query["language"].FirstOrDefault(), Request.Headers.AcceptLanguage.FirstOrDefault());
    [HttpGet("meal-plan-categories")] public async Task<ActionResult<ApiResponse<IReadOnlyList<PlanCategoryResponse>>>> Categories(CancellationToken ct) => Ok(ApiResponse<IReadOnlyList<PlanCategoryResponse>>.Ok(await queries.GetPlanCategoriesAsync(Language(), DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), ct)));
    [HttpGet("meal-plans/{planId:guid}")] public async Task<ActionResult<ApiResponse<MealPlanResponse>>> Plan(Guid planId, CancellationToken ct) { var x = await queries.GetPlanAsync(planId, Language(), DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), ct); return x is null ? NotFound() : Ok(ApiResponse<MealPlanResponse>.Ok(x)); }
    [HttpGet("meal-plans/{planId:guid}/calendar")] public async Task<ActionResult<ApiResponse<IReadOnlyList<CalendarDayResponse>>>> Calendar(Guid planId, [FromQuery] DateOnly? startDate, [FromQuery] int numberOfDays = 7, CancellationToken ct = default) { if (numberOfDays is < 1 or > 31) return BadRequest(new ProblemDetails { Title = "numberOfDays must be between 1 and 31." }); var rows = await queries.GetCalendarAsync(planId, startDate ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), numberOfDays, Language(), ct); return rows is null ? NotFound() : Ok(ApiResponse<IReadOnlyList<CalendarDayResponse>>.Ok(rows)); }
    [HttpGet("meal-plans/{planId:guid}/meals")] public async Task<ActionResult<ApiResponse<IReadOnlyList<MealCardResponse>>>> Meals(Guid planId, [FromQuery] MealListQuery query, CancellationToken ct) { if (query.Page < 1 || query.PageSize is < 1 or > 100) return BadRequest(new ProblemDetails { Title = "Invalid pagination." }); var rows = await queries.GetPlanMealsAsync(planId, query, Language(), clock.GetUtcNow(), ct); return rows is null ? NotFound() : Ok(ApiResponse<IReadOnlyList<MealCardResponse>>.Ok(rows.Items, rows.Meta)); }
    [HttpGet("meals/{mealItemId:guid}")] public async Task<ActionResult<ApiResponse<MealDetailsResponse>>> Meal(Guid mealItemId, CancellationToken ct) { var x = await queries.GetMealAsync(mealItemId, Language(), clock.GetUtcNow(), ct); return x is null ? NotFound() : Ok(ApiResponse<MealDetailsResponse>.Ok(x)); }
    [HttpGet("meal-types")] public async Task<ActionResult<ApiResponse<IReadOnlyList<MealTypeResponse>>>> Types(CancellationToken ct) => Ok(ApiResponse<IReadOnlyList<MealTypeResponse>>.Ok(await queries.GetMealTypesAsync(Language(), ct)));
    [HttpGet("meals/search")] public async Task<ActionResult<ApiResponse<IReadOnlyList<MealSearchResponse>>>> Search([FromQuery] MealSearchQuery query, CancellationToken ct) { if (query.Page < 1 || query.PageSize is < 1 or > 100) return BadRequest(new ProblemDetails { Title = "Invalid pagination." }); var rows = await queries.SearchMealsAsync(query, Language(), clock.GetUtcNow(), ct); return Ok(ApiResponse<IReadOnlyList<MealSearchResponse>>.Ok(rows.Items, rows.Meta)); }
}
