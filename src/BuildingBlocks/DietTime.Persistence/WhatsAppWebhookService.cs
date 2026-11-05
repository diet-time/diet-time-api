using DietTime.Application;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;
namespace DietTime.Persistence;

public sealed class WhatsAppWebhookService(DietTimeDbContext db, StaffOrderService orders, TimeProvider clock)
{
    public async Task<string> ReviewAsync(string incomingSid, string originalSid, string sender, Guid actor, string action, CancellationToken ct)
    {
        var mapping = await db.OrderNotifications.AsNoTracking().SingleOrDefaultAsync(n => n.MessageSid == originalSid &&
            n.EventCode == "ORDER_RECEIVED" && n.Audience == "STAFF", ct);
        if (mapping == null || Normalize(mapping.RecipientPhone) != Normalize(sender)) return "unmatched_reply";
        if (!await (from userRole in db.UserRoles join role in db.Roles on userRole.RoleId equals role.Id
            where userRole.UserId == actor && role.NormalizedName == "ADMIN" select userRole).AnyAsync(ct)) return "unauthorized_admin_identity";
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({incomingSid},0))", ct);
        var seen = await db.Set<WhatsAppWebhookReceipt>().AsNoTracking().SingleOrDefaultAsync(r => r.MessageSid == incomingSid, ct);
        if (seen != null) { await tx.CommitAsync(ct); return "already_processed"; }
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM public.orders WHERE id={mapping.OrderId} FOR UPDATE", ct);
        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == mapping.OrderId, ct);
        var outcome = "already_processed";
        if (order.Status is "PENDING_APPROVAL" or "PENDING") {
            await tx.CreateSavepointAsync("whatsapp_review", ct);
            try {
                await orders.ChangeAsync(order.Id, action, order.RowVersion, $"{action} via verified admin WhatsApp", actor, ct);
                outcome = "processed";
            }
            catch (OrderReviewException e) {
                await tx.RollbackToSavepointAsync("whatsapp_review", ct);
                db.ChangeTracker.Clear(); outcome = e.Code;
            }
        }
        db.Add(new WhatsAppWebhookReceipt { Id = Guid.NewGuid(), MessageSid = incomingSid, OrderId = mapping.OrderId,
            ActorId = actor, Action = action, Outcome = outcome, CreatedAt = clock.GetUtcNow() });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return outcome;
    }
    public async Task<bool> StatusAsync(Guid id, string sid, string recipient, string status, string? error, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM public.order_notifications WHERE id={id} FOR UPDATE", ct);
        var message = await db.OrderNotifications.SingleOrDefaultAsync(n => n.Id == id, ct);
        if (message == null) return false;
        await db.Entry(message).ReloadAsync(ct);
        if (Normalize(message.RecipientPhone) != Normalize(recipient) ||
            message.MessageSid != null && message.MessageSid != sid ||
            message.MessageSid == null && message.Status is not ("PROCESSING" or "UNKNOWN")) return false;
        message.MessageSid ??= sid;
        WhatsAppDeliveryState.Apply(message, status, clock.GetUtcNow(), error);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return true;
    }
    private static string Normalize(string? number) => (number ?? "").Replace("whatsapp:", "", StringComparison.OrdinalIgnoreCase).Trim();
}
