using System.Globalization;
using System.Text.Json;
using DietTime.Application;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DietTime.Persistence;

public sealed class WhatsAppNotificationSchedule(DietTimeDbContext db, TimeProvider clock, IOptions<OrderWhatsAppTemplateOptions> options)
{
    public async Task CreateRemindersAsync(CancellationToken ct)
    {
        if (!options.Value.MealRemindersEnabled) return;
        var now = clock.GetUtcNow();
        var today = SubscriptionMenuRules.QatarToday(now);
        var days = await db.Set<SubscriptionServiceDay>().AsNoTracking().Where(d => d.OrderId != null && d.ServiceDate > today &&
            d.ServiceDate <= today.AddDays(2) && d.Status != "CANCELLED" && d.Status != "SKIPPED" &&
            d.CustomerConfirmedAt == null && !d.SelectionLocked && d.SelectionStatus != "COMPLETED" && d.SelectionStatus != "LOCKED" &&
            d.AutoFinalizedAt == null && d.ReviewReason == null && d.Order != null)
            .Select(d => new { Day = d, Profile = d.Order!.CustomerProfileId }).ToArrayAsync(ct);
        foreach (var candidate in days.OrderBy(d => d.Day.ServiceDate).ThenBy(d => d.Day.Id))
        {
            var day = candidate.Day;
            var cutoff = day.SelectionCutoffAt ?? SubscriptionMenuRules.TomorrowCutoff(day.ServiceDate);
            if (now >= cutoff || now < cutoff.AddMinutes(-Math.Clamp(options.Value.ReminderMinutesBeforeCutoff, 1, 1440))) continue;
            var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == day.OrderId, ct);
            if (!OrderWorkflowPolicy.CanFulfill(order) || await PausedAsync(order.Id, day.ServiceDate, ct)) continue;
            var user = await db.CustomerProfiles.Where(p => p.Id == candidate.Profile).Select(p => p.UserId).SingleAsync(ct);
            var key = $"reminder:{user}:{day.ServiceDate:yyyy-MM-dd}";
            var payload = JsonSerializer.Serialize(new Dictionary<string, string> {
                ["serviceDayId"] = day.Id.ToString(), ["serviceDate"] = Display(day.ServiceDate),
                ["deadline"] = cutoff.ToOffset(TimeSpan.FromHours(3)).ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture) + " Qatar" });
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO public.order_notifications(id,order_id,event_code,audience,status,attempts,available_at,created_at,event_key,payload_json)
                VALUES ({Guid.NewGuid()},{order.Id},'MEAL_REMINDER','CUSTOMER','PENDING',0,{now},{now},{key},{payload}::jsonb)
                ON CONFLICT (event_key) WHERE event_key IS NOT NULL DO NOTHING
                """, ct);
        }
    }
    public Task<bool> PausedAsync(Guid id, DateOnly date, CancellationToken ct) => db.Set<SubscriptionPause>().AnyAsync(
        p => p.OrderId == id && p.PauseStartDate <= date && p.ResumeDate > date, ct);
    public static string Display(DateOnly date) => date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

    // Called inside the schedule mutation transaction; preview paths never call this helper.
    public static async Task RecordPauseAsync(DietTimeDbContext db, Order order, SubscriptionPause pause, int remaining, DateTimeOffset now, CancellationToken ct)
    {
        var context = new Dictionary<string, string> {
            ["pauseStart"] = Display(pause.PauseStartDate), ["resumeDate"] = Display(pause.ResumeDate),
            ["remainingDays"] = remaining.ToString(CultureInfo.InvariantCulture), ["endDate"] = Display(order.EndDate),
            ["pauseId"] = pause.Id.ToString() };
        db.OrderNotifications.Add(new OrderNotification { Id = Guid.NewGuid(), OrderId = order.Id, EventKey = $"pause:{pause.Id}",
            EventCode = "PACKAGE_PAUSED", PayloadJson = JsonSerializer.Serialize(context), AvailableAt = now, CreatedAt = now });
        await ScheduleResumeAsync(db, order, pause, remaining, now, ct);
    }
    public static async Task ScheduleResumeAsync(DietTimeDbContext db, Order order, SubscriptionPause pause, int remaining, DateTimeOffset now, CancellationToken ct)
    {
        var key = $"resume:{pause.Id}";
        var pending = db.OrderNotifications.Local.FirstOrDefault(n => n.EventKey == key) ??
            await db.OrderNotifications.SingleOrDefaultAsync(n => n.EventKey == key, ct);
        if (pending?.SentAt != null || pending?.Status is "SENT" or "DELIVERED" or "READ" or "UNKNOWN") return;
        if (pending == null) {
            pending = new OrderNotification { Id = Guid.NewGuid(), OrderId = order.Id, EventKey = key, EventCode = "PACKAGE_RESUMED", CreatedAt = now };
            db.OrderNotifications.Add(pending);
        }
        pending.Status = pause.ResumeDate <= pause.PauseStartDate ? "SKIPPED" : "PENDING";
        pending.AvailableAt = new DateTimeOffset(pause.ResumeDate.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3)).ToUniversalTime();
        pending.PayloadJson = JsonSerializer.Serialize(new Dictionary<string, string> {
            ["resumeDate"] = Display(pause.ResumeDate), ["remainingDays"] = remaining.ToString(CultureInfo.InvariantCulture),
            ["endDate"] = Display(order.EndDate), ["pauseId"] = pause.Id.ToString() });
    }
}
