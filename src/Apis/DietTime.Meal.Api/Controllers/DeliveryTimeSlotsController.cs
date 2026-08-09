using Asp.Versioning;
using DietTime.Application;
using DietTime.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DietTime.Meal.Api.Controllers;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/delivery-time-slots")]
public sealed class DeliveryTimeSlotsController(IDeliveryTimeSlotService slots) : ControllerBase
{
    /// <summary>Returns active delivery time slots in configured display order.</summary>
    /// <remarks>
    /// Example response:
    /// <code>{ "items": [{ "id": "00000000-0000-0000-0000-000000000001", "code": "MORNING", "name": "Morning", "nameAr": "صباحاً", "startTime": "09:00:00", "endTime": "11:00:00" }] }</code>
    /// </remarks>
    [HttpGet]
    [AllowAnonymous]
    [Produces("application/json")]
    [ProducesResponseType(typeof(DeliveryTimeSlotListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<DeliveryTimeSlotListResponse>> Get(CancellationToken cancellationToken) =>
        Ok(new DeliveryTimeSlotListResponse(await slots.GetActiveAsync(cancellationToken)));
}
