using DietTime.Application;
using DietTime.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DietTime.Meal.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/admin/integrations/whatsapp")]
public sealed class WhatsAppIntegrationController(
    IWhatsAppService whatsApp,
    IOptions<WhatsAppOptions> options,
    IWebHostEnvironment environment) : ControllerBase
{
    [HttpPost("test")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SendTest(CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment()) return NotFound();

        var notification = new NewOrderWhatsAppNotification
        {
            OrderId = Guid.Empty,
            OrderNumber = $"TEST-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
            CustomerName = "Development Test Customer",
            CustomerMobile = "+97400000000",
            MealPlanName = "Development Test Plan",
            Duration = "1 Day",
            MealsPerDay = 1,
            StartDate = DateOnly.FromDateTime(DateTime.UtcNow),
            DeliveryDays = DateTime.UtcNow.ToString("ddd", System.Globalization.CultureInfo.InvariantCulture),
            DeliveryAddress = "Development test address",
            TotalAmount = 1m,
            Currency = "QAR",
            OrderStatus = "CONFIRMED"
        };
        var result = await whatsApp.SendNewOrderNotificationAsync(
            notification, cancellationToken);
        var destination = WhatsAppPhoneNumber.Normalize(options.Value.OperationsNumber);

        return result.Success
            ? Ok(new { success = true, messageId = result.MessageId, destination })
            : StatusCode(StatusCodes.Status502BadGateway, new
            {
                success = false,
                errorCode = result.ErrorCode,
                message = "Unable to send WhatsApp test notification."
            });
    }
}
