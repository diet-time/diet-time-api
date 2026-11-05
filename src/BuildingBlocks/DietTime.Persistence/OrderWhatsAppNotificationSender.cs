using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DietTime.Application;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DietTime.Persistence;

// Existing durable outbox, shared by checkout, staff actions and subscription changes.
public sealed class OrderWhatsAppNotificationSender(DietTimeDbContext db, ITwilioWhatsAppService whatsApp,
    IOptions<OrderWhatsAppTemplateOptions> templates, IWhatsAppRuntimeConfiguration runtime,
    WhatsAppNotificationSchedule schedule, TimeProvider clock, ILogger<OrderWhatsAppNotificationSender> logger) : IOrderNotificationSender
{
    public Task SendAsync(Guid orderId, CancellationToken ct) => ProcessAsync(orderId, ct);
    public async Task SendPendingAsync(CancellationToken ct) {
        await schedule.CreateRemindersAsync(ct);
        await ProcessAsync(Guid.Empty, ct);
    }
    private async Task ProcessAsync(Guid orderId, CancellationToken ct)
    {
        var expired = clock.GetUtcNow();
        // A worker may have died after Twilio accepted a request. Never blindly resend that request.
        await db.OrderNotifications.Where(n => n.Status == "PROCESSING" && n.LockedUntil <= expired)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Status, "UNKNOWN").SetProperty(n => n.LastError, "send_outcome_unknown_check_twilio"), ct);
        for (var batch = 0; batch < 20; batch++)
        {
            var now = clock.GetUtcNow();
            OrderNotification? message;
            await using (var tx = await db.Database.BeginTransactionAsync(ct)) {
                message = (await db.OrderNotifications.FromSqlInterpolated($"""
                    SELECT * FROM public.order_notifications
                    WHERE ({orderId} = '00000000-0000-0000-0000-000000000000'::uuid OR order_id = {orderId})
                      AND status IN ('PENDING','WAITING_CONFIGURATION') AND available_at <= {now}
                      AND (locked_until IS NULL OR locked_until <= {now})
                    ORDER BY available_at, id LIMIT 1 FOR UPDATE SKIP LOCKED
                    """).ToListAsync(ct)).SingleOrDefault();
                if (message is null) return;
                await db.Entry(message).ReloadAsync(ct);
                message.Status = "PROCESSING"; message.LockedUntil = now.AddMinutes(3); message.Attempts++;
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            }
            WhatsAppSendResult? result = null;
            try {
                var order = await db.Orders.AsNoTracking().Include(o => o.Meals).SingleAsync(o => o.Id == message.OrderId, ct);
                var context = JsonSerializer.Deserialize<Dictionary<string, string>>(message.PayloadJson) ?? new();
                context["eventDate"] = message.CreatedAt.ToOffset(TimeSpan.FromHours(3)).ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
                if (!await EligibleAsync(message, order, context, ct)) { message.Status = "SKIPPED"; continue; }
                if (!runtime.Enabled || !ValidBaseUrl(runtime.PublicWebhookBaseUrl)) { WaitForConfiguration(message, now); continue; }
                var customer = await (from profile in db.CustomerProfiles.AsNoTracking() join user in db.Users.AsNoTracking() on profile.UserId equals user.Id
                    where profile.Id == order.CustomerProfileId select new { Name = profile.PreferredName ?? user.UserName ?? "Name not recorded", user.PhoneNumber }).SingleAsync(ct);
                var types = await db.MealTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Code, ct);
                var meals = order.Meals.Where(m => !MealTypeClassification.IsSnack(types[m.MealTypeId])).Sum(m => m.Quantity);
                var snacks = order.Meals.Where(m => MealTypeClassification.IsSnack(types[m.MealTypeId])).Sum(m => m.Quantity);
                var template = WhatsAppTemplateRegistry.Build(message, order, customer.Name, customer.PhoneNumber ?? "",
                    $"{meals} Meals + {snacks} Snacks", WhatsAppTemplateRegistry.Location(order), context, templates.Value, runtime);
                message.TemplateName = template.Name; message.ContentSid = template.ContentSid;
                message.RecipientPhone = message.Audience == "STAFF" ? runtime.OperationsNumber : customer.PhoneNumber;
                if (!Regex.IsMatch(template.ContentSid ?? "", @"\AHX[0-9a-fA-F]{32}\z")) { WaitForConfiguration(message, now); continue; }
                if (!Regex.IsMatch(message.RecipientPhone ?? "", @"\A(?:whatsapp:)?\+[1-9][0-9]{7,14}\z")) {
                    message.Status = "FAILED"; message.LastError = "recipient_phone_missing_or_invalid"; continue;
                }
                // Persist routing before making the provider call so a fast signed callback can bind its SID.
                await db.SaveChangesAsync(ct);
                var callback = runtime.PublicWebhookBaseUrl.TrimEnd('/') + $"/api/webhooks/twilio/whatsapp/status?notificationId={message.Id}";
                result = await whatsApp.SendTemplateAsync(new(message.RecipientPhone!, template.ContentSid!, template.Variables, callback), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (InvalidOperationException error) when (error.Message == "required_notification_data_missing") {
                message.Status = "FAILED"; message.LastError = error.Message;
            }
            catch (Exception error) {
                // Exceptions after a request may be ambiguous. Retain the event for callback/reconciliation.
                message.Status = "UNKNOWN"; message.LastError = "notification_outcome_unknown";
                logger.LogError(error, "WhatsApp notification processing failed. NotificationId={NotificationId}", message.Id);
            }
            finally {
                if (!ct.IsCancellationRequested) {
                    if (result is not null) await CompleteAsync(message, result, ct);
                    else { message.LockedUntil = null; await db.SaveChangesAsync(ct); }
                }
            }
        }
    }
    private async Task<bool> EligibleAsync(OrderNotification message, Order order, Dictionary<string, string> context, CancellationToken ct)
    {
        if (message.EventCode is "PACKAGE_PAUSED" or "PACKAGE_RESUMED" or "MEAL_REMINDER" && !OrderWorkflowPolicy.CanFulfill(order)) return false;
        if (message.EventCode == "ORDER_APPROVED" && order.Status is "CANCELLED" or "REJECTED") return false;
        if (message.EventCode == "PACKAGE_RESUMED") {
            if (!Guid.TryParse(context.GetValueOrDefault("pauseId"), out var pauseId)) return false;
            var pause = await db.Set<SubscriptionPause>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == pauseId && p.OrderId == order.Id, ct);
            if (pause == null || pause.ResumeDate > SubscriptionMenuRules.QatarToday(clock.GetUtcNow())) return false;
            context["remainingDays"] = (await db.Set<SubscriptionServiceDay>().CountAsync(d => d.OrderId == order.Id &&
                d.Status != "CANCELLED" && d.Status != "SKIPPED" && d.Status != "DELIVERED" && d.Status != "COLLECTED", ct)).ToString(CultureInfo.InvariantCulture);
        }
        if (message.EventCode != "MEAL_REMINDER") return true;
        if (!templates.Value.MealRemindersEnabled || !Guid.TryParse(context.GetValueOrDefault("serviceDayId"), out var dayId)) return false;
        var day = await db.Set<SubscriptionServiceDay>().AsNoTracking().SingleOrDefaultAsync(d => d.Id == dayId && d.OrderId == order.Id, ct);
        return day != null && day.Status is "PLANNED" or "CONFIRMED" && !day.SelectionLocked && day.SelectionStatus is not ("COMPLETED" or "LOCKED") && day.ReviewReason == null && day.CustomerConfirmedAt == null && day.AutoFinalizedAt == null &&
            clock.GetUtcNow() < (day.SelectionCutoffAt ?? SubscriptionMenuRules.TomorrowCutoff(day.ServiceDate)) &&
            !await schedule.PausedAsync(order.Id, day.ServiceDate, ct);
    }
    private static void WaitForConfiguration(OrderNotification message, DateTimeOffset now) {
        message.Status = "WAITING_CONFIGURATION"; message.Attempts--; message.LastError = "notification_configuration_missing_or_disabled";
        message.AvailableAt = now.AddMinutes(15);
    }
    private async Task CompleteAsync(OrderNotification message, WhatsAppSendResult result, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM public.order_notifications WHERE id={message.Id} FOR UPDATE", ct);
        await db.Entry(message).ReloadAsync(ct);
        if (result.MessageId != null) message.MessageSid ??= result.MessageId;
        if (message.Status is not ("DELIVERED" or "READ")) {
            if (result.Success) WhatsAppDeliveryState.Apply(message, result.Status ?? "queued", clock.GetUtcNow());
            else if (result.MessageId != null) WhatsAppDeliveryState.Apply(message, result.Status ?? "failed", clock.GetUtcNow(), Error(result));
            else if (result.ErrorCode is "timeout" or "http_error" or "invalid_provider_response") {
                message.Status = "UNKNOWN"; message.LastError = "send_outcome_unknown_check_twilio";
            }
            else if (result.ErrorCode is "twilio_whatsapp_disabled" or "twilio_configuration_invalid") WaitForConfiguration(message, clock.GetUtcNow());
            else {
                message.LastError = Error(result);
                var transient = result.ProviderHttpStatusCode is 408 or 429 or >= 500 || result.ErrorCode is "429" or "500" or "502" or "503" or "20429";
                message.Status = transient && message.Attempts < 5 ? "PENDING" : "FAILED";
                message.AvailableAt = clock.GetUtcNow().AddSeconds(30 * Math.Pow(2, Math.Min(message.Attempts - 1, 6)));
            }
        }
        message.LockedUntil = null;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    private static string Error(WhatsAppSendResult result) {
        var value = $"{result.ErrorCode}: {result.ErrorMessage}";
        return value[..Math.Min(value.Length, 500)];
    }
    public static bool ValidBaseUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && string.IsNullOrEmpty(uri.UserInfo);
}
