using System.Globalization;
using Asp.Versioning;
using DietTime.Application;
using DietTime.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DietTime.Meal.Api.Controllers;

[ApiController]
[ApiVersion(1)]
[Authorize(Roles = "Admin,Operations")]
[Route("api/v{version:apiVersion}/admin/orders/delivery-calendar")]
public sealed class AdminDeliveryCalendarController(IAdminDeliveryCalendarService calendar) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<AdminDeliveryCalendarResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetMonth(
        [FromQuery] string month,
        [FromQuery] Guid? planId = null,
        [FromQuery] string? orderStatus = null,
        CancellationToken cancellationToken = default)
    {
        if (!DateOnly.TryParseExact(
                $"{month}-01",
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var startDate))
        {
            ModelState.AddModelError(nameof(month), "month must use yyyy-MM format.");
            return ValidationProblem(ModelState);
        }
        if (orderStatus?.Trim().Length > 30)
        {
            ModelState.AddModelError(nameof(orderStatus), "orderStatus must contain at most 30 characters.");
            return ValidationProblem(ModelState);
        }

        var endDate = startDate.AddMonths(1).AddDays(-1);
        var response = await calendar.GetMonthAsync(
            startDate, endDate, planId, orderStatus, cancellationToken);
        return Ok(ApiResponse<AdminDeliveryCalendarResponse>.Ok(response));
    }
}
