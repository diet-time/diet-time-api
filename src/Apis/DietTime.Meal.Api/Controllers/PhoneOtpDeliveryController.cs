using Asp.Versioning;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DietTime.Meal.Api.Controllers;

[ApiController]
[ApiVersion("1")]
[Route("api/v{version:apiVersion}/auth/phone-otp")]
public sealed class PhoneOtpDeliveryController(IPhoneOtpAuthenticationService otp) : ControllerBase
{
    [HttpPost("request")]
    [AllowAnonymous]
    [EnableRateLimiting("phone-otp")]
    public async Task<IActionResult> RequestCode([FromBody] PhoneOtpDeliveryRequest request, CancellationToken ct)
    {
        if (!PhoneOtpNumbers.TryNormalize(request.PhoneNumber, out var phone) || request.Channel != "whatsapp")
            return BadRequest(new ProblemDetails { Title = "Enter a valid international phone number and use WhatsApp." });
        var result = await otp.SendAsync(phone, ct);
        if (result.StatusCode != 200) {
            if (result.RetryAfterSeconds > 0) Response.Headers["Retry-After"] = result.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return StatusCode(result.StatusCode, new ProblemDetails {
                Title = result.StatusCode switch {
                    429 => "Too many attempts. Try again later.",
                    400 => "Unable to request verification for this number.",
                    _ => "WhatsApp verification is unavailable. Try again later."
                }
            });
        }
        return Ok(ApiResponse<PhoneOtpDeliveryResponse>.Ok(new(result.RequestId!, result.ExpiresInSeconds, result.ResendAfterSeconds)));
    }
}

public sealed record PhoneOtpDeliveryRequest(string PhoneNumber, string Channel = "whatsapp");
public sealed record PhoneOtpDeliveryResponse(string RequestId, int ExpiresInSeconds, int ResendAfterSeconds);
