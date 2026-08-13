using DietTime.Application;
using DietTime.Contracts;
using DietTime.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DietTime.Meal.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/admin/integrations/whatsapp")]
public sealed class WhatsAppIntegrationController(
    ITwilioWhatsAppService twilioWhatsApp) : ControllerBase
{
    [HttpPost("twilio/messages")]
    [ProducesResponseType(typeof(SendWhatsAppTemplateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> SendTwilioTemplate(
        [FromBody] SendTwilioWhatsAppTemplateRequest request,
        CancellationToken cancellationToken)
    {
        if (!TwilioWhatsAppPhoneNumber.IsValid(request.To))
            ModelState.AddModelError(nameof(request.To),
                "to must use E.164 format, for example +97455555555.");
        if (string.IsNullOrWhiteSpace(request.ContentSid) ||
            !request.ContentSid.StartsWith("HX", StringComparison.Ordinal) ||
            request.ContentSid.Length > 64)
            ModelState.AddModelError(nameof(request.ContentSid),
                "contentSid must be a valid Twilio Content SID beginning with HX.");
        if (request.ContentVariables is null || request.ContentVariables.Count == 0)
            ModelState.AddModelError(nameof(request.ContentVariables),
                "At least one content variable is required.");
        else if (request.ContentVariables.Any(pair =>
                     !int.TryParse(pair.Key, out var index) || index < 1 ||
                     pair.Value is null || pair.Value.Length > 1000))
            ModelState.AddModelError(nameof(request.ContentVariables),
                "Variable keys must be positive numbers and values must contain at most 1000 characters.");
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var result = await twilioWhatsApp.SendTemplateAsync(
            new(request.To, request.ContentSid, request.ContentVariables!), cancellationToken);
        if (result.Success)
            return Ok(new SendWhatsAppTemplateResponse(true, result.MessageId));

        return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
        {
            Title = "Twilio WhatsApp message failed",
            Detail = result.ErrorMessage,
            Extensions = { ["providerCode"] = result.ErrorCode }
        });
    }

}
