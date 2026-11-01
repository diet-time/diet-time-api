using System.Security.Claims;
using Asp.Versioning;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DietTime.Meal.Api.Controllers;

[ApiController]
[ApiVersion(1)]
[Authorize(Roles = FulfillmentStaffRoles.Write)]
[Route("api/admin/subscription-service-days")]
[Route("api/v{version:apiVersion}/admin/subscription-service-days")]
public sealed class ServiceDayFulfillmentController(ServiceDayFulfillmentService fulfillment) : ControllerBase
{
    [HttpPatch("{serviceDayId:guid}/fulfillment")]
    public async Task<IActionResult> Update(Guid serviceDayId, ServiceDayFulfillmentRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id)) return Unauthorized();
        Guid? actor = id;
        try { return Ok(ApiResponse<ServiceDayFulfillmentResponse>.Ok(await fulfillment.UpdateAsync(serviceDayId, request.Status, actor, ct))); }
        catch (FulfillmentException error) { return StatusCode(error.Status, new ProblemDetails { Status = error.Status, Title = error.Code, Detail = error.Message, Extensions = { ["code"] = error.Code } }); }
    }
}
