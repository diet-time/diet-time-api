using System.Security.Claims;
using Asp.Versioning;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace DietTime.Meal.Api.Controllers;
[ApiController,ApiVersion(1),Authorize(Roles=FulfillmentStaffRoles.Write)]
[Route("api/admin/notifications")]
[Route("api/v{version:apiVersion}/admin/notifications")]
public sealed class StaffNotificationsController(StaffNotificationService notifications) : ControllerBase
{
    private Guid? Actor => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"),out var id) ? id : null;
    [HttpGet]
    public async Task<IActionResult> List(int page=1,int pageSize=20,CancellationToken ct=default) {
        if(Actor is not { } user) return Unauthorized();
        if(page<1 || page>100000 || pageSize<1 || pageSize>50) return BadRequest(new ProblemDetails { Detail="Use a valid page and page size between 1 and 50." });
        return Ok(ApiResponse<StaffNotificationListResponse>.Ok(await notifications.ListAsync(user,User.IsInRole("Admin"),page,pageSize,ct)));
    }
    [HttpGet("unread-count")]
    public async Task<IActionResult> Count(CancellationToken ct) => Actor is { } user ? Ok(ApiResponse<StaffNotificationCountResponse>.Ok(new(await notifications.UnreadAsync(user,User.IsInRole("Admin"),ct)))) : Unauthorized();
    [HttpPatch("{id:guid}/read")]
    public async Task<IActionResult> Read(Guid id,CancellationToken ct) {
        if(Actor is not { } user) return Unauthorized();
        return await notifications.ReadAsync(user,User.IsInRole("Admin"),id,ct) ? NoContent() : NotFound();
    }
    [HttpPatch("read-all")]
    public async Task<IActionResult> ReadAll(CancellationToken ct) {
        if(Actor is not { } user) return Unauthorized();
        await notifications.ReadAllAsync(user,User.IsInRole("Admin"),ct); return NoContent();
    }
}
