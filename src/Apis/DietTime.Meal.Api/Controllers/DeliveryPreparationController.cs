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
[Route("api/admin/delivery-calendar")]
[Route("api/v{version:apiVersion}/admin/delivery-calendar")]
public sealed class DeliveryPreparationController(
    IAdminDeliveryCalendarService calendar,
    IKitchenPreparationReportGenerator reportGenerator,
    ILogger<DeliveryPreparationController> logger) : ControllerBase
{
    [HttpGet("{date}/preparation-summary")]
    [ProducesResponseType(typeof(ApiResponse<DeliveryPreparationSummaryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetPreparationSummary(
        string date,
        CancellationToken cancellationToken)
    {
        if (!TryParseDate(date, out var selectedDate))
            return ValidationProblem(ModelState);

        var summary = await calendar.GetPreparationSummaryAsync(selectedDate, cancellationToken);
        return Ok(ApiResponse<DeliveryPreparationSummaryResponse>.Ok(summary));
    }

    [HttpGet("{date}/preparation-report")]
    [Produces("application/pdf")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetPreparationReport(
        string date,
        CancellationToken cancellationToken)
    {
        if (!TryParseDate(date, out var selectedDate))
            return ValidationProblem(ModelState);

        try
        {
            var summary = await calendar.GetPreparationSummaryAsync(selectedDate, cancellationToken);
            var pdf = await reportGenerator.GenerateAsync(summary, cancellationToken);
            logger.LogInformation(
                "Kitchen preparation report generated. DeliveryDate={DeliveryDate} OrderCount={OrderCount} MealItemCount={MealItemCount}",
                selectedDate, summary.OrderCount, summary.MealItemCount);
            return File(pdf, "application/pdf", $"Kitchen-Preparation-{selectedDate:yyyy-MM-dd}.pdf");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Failed to generate kitchen preparation report. DeliveryDate={DeliveryDate}",
                selectedDate);
            throw;
        }
    }

    private bool TryParseDate(string date, out DateOnly selectedDate)
    {
        if (DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out selectedDate))
            return true;

        ModelState.AddModelError(nameof(date), "date must use yyyy-MM-dd format.");
        return false;
    }
}
