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
[Route("api/admin/dashboard/operations")]
[Route("api/v{version:apiVersion}/admin/dashboard/operations")]
public sealed class OperationsDashboardController(IOperationsDashboardService dashboard) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(OperationsDashboardResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Get(
        [FromQuery] string? date = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryDate(date, out var selectedDate))
            return InvalidDate();
        return Ok(await dashboard.GetAsync(selectedDate, cancellationToken));
    }

    [HttpGet("deliveries")]
    [ProducesResponseType(typeof(DashboardDeliveriesPage), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetDeliveries(
        [FromQuery] string? date = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        if (!TryDate(date, out var selectedDate))
            return InvalidDate();
        if (page < 1 || pageSize is < 1 or > 100)
        {
            ModelState.AddModelError(nameof(page), "page must be at least 1.");
            ModelState.AddModelError(nameof(pageSize), "pageSize must be between 1 and 100.");
            return ValidationProblem(ModelState);
        }
        return Ok(await dashboard.GetDeliveriesAsync(selectedDate, page, pageSize, cancellationToken));
    }

    private bool TryDate(string? value, out DateOnly date)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            date = dashboard.GetBusinessDate();
            return true;
        }
        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out date);
    }

    private IActionResult InvalidDate()
    {
        ModelState.AddModelError("date", "date must use yyyy-MM-dd format.");
        return new ObjectResult(new ValidationProblemDetails(ModelState) { Status = StatusCodes.Status400BadRequest })
        {
            StatusCode = StatusCodes.Status400BadRequest
        };
    }
}
