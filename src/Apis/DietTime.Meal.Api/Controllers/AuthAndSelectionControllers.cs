using Asp.Versioning;
using DietTime.Application;
using DietTime.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DietTime.Meal.Api.Controllers;

[ApiController, ApiVersion(1), Route("api/v{version:apiVersion}/auth")]
public sealed class AuthController(IAuthService auth) : ControllerBase
{
    [AllowAnonymous, HttpPost("register")] public async Task<ActionResult<ApiResponse<TokenResponse>>> Register(RegisterRequest request, CancellationToken ct) { var token = await auth.RegisterAsync(request, ct); return token is null ? Conflict(new ProblemDetails { Title = "Registration failed." }) : Ok(ApiResponse<TokenResponse>.Ok(token)); }
    [AllowAnonymous, HttpPost("login")] public async Task<ActionResult<ApiResponse<TokenResponse>>> Login(LoginRequest request, CancellationToken ct) { var token = await auth.LoginAsync(request, ct); return token is null ? Unauthorized() : Ok(ApiResponse<TokenResponse>.Ok(token)); }
    [AllowAnonymous, HttpPost("refresh")] public async Task<ActionResult<ApiResponse<TokenResponse>>> Refresh(RefreshRequest request, CancellationToken ct) { var token = await auth.RefreshAsync(request, ct); return token is null ? Unauthorized() : Ok(ApiResponse<TokenResponse>.Ok(token)); }
}

[ApiController, ApiVersion(1), Authorize, Route("api/v{version:apiVersion}/meal-selections")]
public sealed class MealSelectionsController(IMealSelectionService selections) : ControllerBase
{
    [HttpPost("validate")] public async Task<ActionResult<ApiResponse<MealSelectionValidationResponse>>> Validate(MealSelectionRequest request, CancellationToken ct) => Ok(ApiResponse<MealSelectionValidationResponse>.Ok(await selections.ValidateAsync(request, DateTimeOffset.UtcNow, ct)));
}
