using Asp.Versioning;
using DietTime.Contracts;
using DietTime.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Text.RegularExpressions;

namespace DietTime.Meal.Api.Controllers;

[ApiController]
[ApiVersion("1")]
[Route("api/v{version:apiVersion}/auth/phone-otp")]
public sealed class PhoneOtpDeliveryController(TwilioPhoneOtpService otp) : ControllerBase
{
    [HttpPost("request")]
    [AllowAnonymous]
    [EnableRateLimiting("phone-otp")]
    public async Task<IActionResult> RequestCode([FromBody] PhoneOtpDeliveryRequest request, CancellationToken ct)
    {
        var phone = request.PhoneNumber?.Trim() ?? "";
        if (!Regex.IsMatch(phone, @"\A\+[1-9][0-9]{7,14}\z") || request.Channel != "whatsapp")
            return BadRequest(new ProblemDetails { Title = "Enter a valid international phone number and use WhatsApp." });
        var result = await otp.SendAsync(phone, ct);
        if (result.StatusCode != 200)
            return StatusCode(result.StatusCode, new ProblemDetails {
                Title = result.StatusCode == 429 ? "Too many attempts. Try again later." : "WhatsApp verification is unavailable. Try again later." });
        return Ok(ApiResponse<PhoneOtpDeliveryResponse>.Ok(new(result.RequestId!, 600)));
    }
}

public sealed record PhoneOtpDeliveryRequest(string PhoneNumber, string Channel = "whatsapp");
public sealed record PhoneOtpDeliveryResponse(string RequestId, int ExpiresInSeconds);
