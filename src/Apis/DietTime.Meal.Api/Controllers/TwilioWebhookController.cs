using DietTime.Application;
using DietTime.Infrastructure;
using DietTime.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Twilio.Security;

namespace DietTime.Meal.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/webhooks/twilio/whatsapp")]
public sealed class TwilioWebhookController(IOptions<TwilioWhatsAppOptions> options, WhatsAppWebhookService webhook) : ControllerBase
{
    [HttpPost]
    [Consumes("application/x-www-form-urlencoded")]
    [RequestSizeLimit(32768)]
    public async Task<IActionResult> Inbound(CancellationToken ct)
    {
        if (!options.Value.AdminActionsEnabled) return NotFound();
        var form = await VerifiedFormAsync(ct);
        if (form is null) return StatusCode(403);
        var configuration = options.Value;
        var sender = Normalize(form.GetValueOrDefault("From"));
        var identity = configuration.AuthorizedAdmins.Where(a => Normalize(a.Phone) == sender && a.UserId != Guid.Empty).ToArray();
        if (!configuration.Enabled || identity.Length != 1 || Normalize(form.GetValueOrDefault("To")) != Normalize(configuration.FromNumber)) return StatusCode(403);
        var payload = form.GetValueOrDefault("ButtonPayload");
        var action = payload switch { "diettime:approve" => "approve", "diettime:reject" => "reject", _ => null };
        var sid = form.GetValueOrDefault("MessageSid");
        var original = form.GetValueOrDefault("OriginalRepliedMessageSid");
        if (action == null || !TwilioMessageSid.IsValid(sid) || !TwilioMessageSid.IsValid(original)) return Content("<Response />", "application/xml");
        var outcome = await webhook.ReviewAsync(sid!, original!, sender, identity[0].UserId, action, ct);
        var text = outcome == "processed" ? "Order decision saved. This does not record or collect a payment." :
            outcome == "already_processed" ? "This order reply has already been processed." : "Order not changed. Please review it in the Diet Time dashboard.";
        return Content($"<Response><Message>{text}</Message></Response>", "application/xml");
    }

    [HttpPost("status")]
    [Consumes("application/x-www-form-urlencoded")]
    [RequestSizeLimit(32768)]
    public async Task<IActionResult> Status([FromQuery] Guid notificationId, CancellationToken ct)
    {
        var form = await VerifiedFormAsync(ct);
        if (form is null) return StatusCode(403);
        var sid = form.GetValueOrDefault("MessageSid");
        if (!TwilioMessageSid.IsValid(sid)) return BadRequest();
        var status = form.GetValueOrDefault("MessageStatus") ?? "";
        if (status is not ("accepted" or "queued" or "sending" or "sent" or "delivered" or "read" or "failed" or "undelivered")) return BadRequest();
        var error = string.Join(": ", new[] { form.GetValueOrDefault("ErrorCode"), form.GetValueOrDefault("ErrorMessage") }.Where(s => !string.IsNullOrWhiteSpace(s)));
        await webhook.StatusAsync(notificationId, sid!, form.GetValueOrDefault("To") ?? "", status, error[..Math.Min(error.Length, 500)], ct);
        return NoContent();
    }
    private async Task<Dictionary<string, string>?> VerifiedFormAsync(CancellationToken ct)
    {
        var configuration = options.Value;
        if (!Request.HasFormContentType || string.IsNullOrWhiteSpace(configuration.AuthToken) ||
            !OrderWhatsAppNotificationSender.ValidBaseUrl(configuration.PublicWebhookBaseUrl)) return null;
        var form = await Request.ReadFormAsync(ct);
        // Reject ambiguous duplicate parameters; otherwise validate every original form field.
        if (form.Any(p => p.Value.Count != 1)) return null;
        var values = form.ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal);
        if (values.GetValueOrDefault("AccountSid") != configuration.AccountSid) return null;
        var url = configuration.PublicWebhookBaseUrl.TrimEnd('/') + Request.Path + Request.QueryString;
        return new RequestValidator(configuration.AuthToken).Validate(url, values, Request.Headers["X-Twilio-Signature"].ToString()) ? values : null;
    }
    private static string Normalize(string? number) => (number ?? "").Replace("whatsapp:", "", StringComparison.OrdinalIgnoreCase).Trim();
}
