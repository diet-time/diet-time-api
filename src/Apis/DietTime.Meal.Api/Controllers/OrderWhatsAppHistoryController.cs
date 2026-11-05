using DietTime.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace DietTime.Meal.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/admin/orders/{orderId:guid}/whatsapp-notifications")]
public sealed class OrderWhatsAppHistoryController(DietTimeDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(Guid orderId, CancellationToken ct)
    {
        if (!await db.Orders.AnyAsync(o => o.Id == orderId, ct)) return NotFound();
        return Ok(await db.OrderNotifications.AsNoTracking().Where(n => n.OrderId == orderId).OrderByDescending(n => n.CreatedAt).Take(200)
            .Select(n => new { n.Id, NotificationType = n.EventCode, BusinessEntityType = "ORDER", BusinessEntityId = n.OrderId,
                n.RecipientPhone, n.TemplateName, n.ContentSid, TwilioMessageSid = n.MessageSid, n.Status, ProviderStatus = n.ProviderStatus,
                AttemptCount = n.Attempts, ErrorMessage = n.LastError, n.CreatedAt, n.SentAt, n.DeliveredAt, n.ReadAt }).ToArrayAsync(ct));
    }
}
