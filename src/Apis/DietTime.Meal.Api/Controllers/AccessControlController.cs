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
    public async Task<IActionResult> GetScreens(CancellationToken cancellationToken)
    {
        if (!await HasPermissionAsync("/roles", false, cancellationToken)) return Forbid();
        return Ok(ApiResponse<IReadOnlyList<ScreenPermissionResponse>>.Ok(await service.GetScreensAsync(cancellationToken)));
    }

    [HttpGet("roles")]
    public async Task<IActionResult> GetRoles(CancellationToken cancellationToken)
    {
        var canReadRoles = await HasPermissionAsync("/roles", false, cancellationToken);
        var canReadUsers = await HasPermissionAsync("/users", false, cancellationToken);
        if (!canReadRoles && !canReadUsers) return Forbid();
        return Ok(ApiResponse<IReadOnlyList<AccessRoleResponse>>.Ok(await service.GetRolesAsync(cancellationToken)));
    }

    [HttpPost("roles")]
    public async Task<IActionResult> CreateRole([FromBody] SaveAccessRoleRequest request, CancellationToken cancellationToken)
    {
        if (!await HasPermissionAsync("/roles", true, cancellationToken)) return Forbid();
        var id = await service.CreateRoleAsync(request, Actor(), cancellationToken);
        return CreatedAtAction(nameof(GetRoles), new { id }, id);
    }

    [HttpPut("roles/{roleId:guid}")]
    public async Task<IActionResult> UpdateRole(Guid roleId, [FromBody] SaveAccessRoleRequest request, CancellationToken cancellationToken)
    {
        if (!await HasPermissionAsync("/roles", true, cancellationToken)) return Forbid();
        return await service.UpdateRoleAsync(roleId, request, Actor(), cancellationToken) ? NoContent() : NotFound();
    }

    [HttpGet("users")]
    public async Task<IActionResult> GetUsers(CancellationToken cancellationToken)
    {
        if (!await HasPermissionAsync("/users", false, cancellationToken)) return Forbid();
        return Ok(ApiResponse<IReadOnlyList<AccessUserResponse>>.Ok(await service.GetUsersAsync(cancellationToken)));
    }

    [HttpPost("users")]
    public async Task<IActionResult> CreateUser([FromBody] CreateAccessUserRequest request, CancellationToken cancellationToken)
    {
        if (!await HasPermissionAsync("/users", true, cancellationToken)) return Forbid();
        var id = await service.CreateUserAsync(request, Actor(), cancellationToken);
        return CreatedAtAction(nameof(GetUsers), new { id }, id);
    }

    [HttpPut("users/{profileId:guid}")]
    public async Task<IActionResult> UpdateUser(Guid profileId, [FromBody] UpdateAccessUserRequest request, CancellationToken cancellationToken)
    {
        if (!await HasPermissionAsync("/users", true, cancellationToken)) return Forbid();
        return await service.UpdateUserAsync(profileId, request, Actor(), cancellationToken) ? NoContent() : NotFound();
    }

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

    private async Task<bool> HasPermissionAsync(string routeUrl, bool requireWrite, CancellationToken cancellationToken) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) &&
        await service.HasScreenPermissionAsync(userId, routeUrl, requireWrite, cancellationToken);
}
