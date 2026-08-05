using Asp.Versioning;
using DietTime.Application;
using DietTime.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace DietTime.Meal.Api.Controllers;

[ApiController]
[ApiVersion("1")]
[Route("api/v{version:apiVersion}/access-control")]
[Authorize]
public sealed class AccessControlController(IAccessControlService service) : ControllerBase
{
    [HttpGet("screens")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetScreens(CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<ScreenPermissionResponse>>.Ok(await service.GetScreensAsync(cancellationToken)));

    [HttpGet("roles")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetRoles(CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<AccessRoleResponse>>.Ok(await service.GetRolesAsync(cancellationToken)));

    [HttpPost("roles")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> CreateRole([FromBody] SaveAccessRoleRequest request, CancellationToken cancellationToken)
    {
        var id = await service.CreateRoleAsync(request, Actor(), cancellationToken);
        return CreatedAtAction(nameof(GetRoles), new { id }, id);
    }

    [HttpPut("roles/{roleId:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateRole(Guid roleId, [FromBody] SaveAccessRoleRequest request, CancellationToken cancellationToken) =>
        await service.UpdateRoleAsync(roleId, request, Actor(), cancellationToken) ? NoContent() : NotFound();

    [HttpGet("users")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetUsers(CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<AccessUserResponse>>.Ok(await service.GetUsersAsync(cancellationToken)));

    [HttpPost("users")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> CreateUser([FromBody] CreateAccessUserRequest request, CancellationToken cancellationToken)
    {
        var id = await service.CreateUserAsync(request, Actor(), cancellationToken);
        return CreatedAtAction(nameof(GetUsers), new { id }, id);
    }

    [HttpPut("users/{profileId:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateUser(Guid profileId, [FromBody] UpdateAccessUserRequest request, CancellationToken cancellationToken) =>
        await service.UpdateUserAsync(profileId, request, Actor(), cancellationToken) ? NoContent() : NotFound();

    [HttpGet("me/screens")]
    [Authorize]
    public async Task<IActionResult> GetMyScreens(CancellationToken cancellationToken)
    {
        var subject = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(subject, out var userId)
            ? Ok(ApiResponse<IReadOnlyList<ScreenPermissionResponse>>.Ok(await service.GetUserScreensAsync(userId, cancellationToken)))
            : Unauthorized();
    }

    private string Actor() => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name ?? "SYSTEM";
}
