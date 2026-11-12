using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;
namespace DietTime.Persistence;

public sealed class StaffNotificationService(DietTimeDbContext db,TimeProvider clock)
{
    private IQueryable<StaffNotification> Visible(Guid user,bool admin) {
        var types=admin ? StaffNotificationRules.AdminEvents : Array.Empty<string>();
        return db.StaffNotifications.Where(n=>n.RecipientUserId==user && types.Contains(n.EventType));
    }
    public async Task<StaffNotificationListResponse> ListAsync(Guid user,bool admin,int page,int size,CancellationToken ct) {
        var query=Visible(user,admin).AsNoTracking();
        var total=await query.CountAsync(ct);
        var rows=await query.OrderByDescending(n=>n.CreatedAt).ThenByDescending(n=>n.Id).Skip((page-1)*size).Take(size).ToArrayAsync(ct);
        var orderIds=rows.Select(n=>n.OrderId).Distinct().ToArray();
        var customers=await (from order in db.Orders.AsNoTracking()
            join customer in db.CustomerProfiles.AsNoTracking() on order.CustomerProfileId equals customer.Id
            join profile in db.UserProfiles.AsNoTracking() on customer.UserId equals (Guid?)profile.UserId into profiles
            from profile in profiles.DefaultIfEmpty()
            where orderIds.Contains(order.Id)
            select new { OrderId=order.Id, customer.PreferredName,
                FirstName=profile == null ? null : profile.FirstName,
                LastName=profile == null ? null : profile.LastName }).ToArrayAsync(ct);
        var names=customers.ToDictionary(c=>c.OrderId,c=> {
            var fullName=$"{c.FirstName} {c.LastName}".Trim();
            return fullName.Length>0 ? fullName : string.IsNullOrWhiteSpace(c.PreferredName) ? null : c.PreferredName.Trim();
        });
        return new(rows.Select(n=> {
            var name=names.GetValueOrDefault(n.OrderId);
            // Enrich existing inbox rows at read time without rewriting history or adding schema fields.
            var content=StaffNotificationRules.Content(n.EventType,n.OrderNumber,n.ServiceDate,name);
            return new StaffNotificationResponse(n.Id,n.EventType,content.Title,content.Message,n.OrderId,n.OrderNumber,n.ServiceDayId,n.ServiceDate,n.CreatedAt,n.ReadAt,StaffNotificationRules.Target(n.OrderId,n.ServiceDayId,n.ServiceDate),name);
        }).ToArray(),page,size,total);
    }
    public Task<int> UnreadAsync(Guid user,bool admin,CancellationToken ct) => Visible(user,admin).CountAsync(n=>n.ReadAt==null,ct);
    public async Task<bool> ReadAsync(Guid user,bool admin,Guid id,CancellationToken ct) {
        var query=Visible(user,admin).Where(n=>n.Id==id);
        var now=clock.GetUtcNow();
        await query.Where(n=>n.ReadAt==null).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.ReadAt,now),ct);
        return await query.AnyAsync(ct);
    }
    public Task<int> ReadAllAsync(Guid user,bool admin,CancellationToken ct) {
        var now=clock.GetUtcNow();
        return Visible(user,admin).Where(n=>n.ReadAt==null).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.ReadAt,now),ct);
    }

    public async Task ProjectAsync(CancellationToken ct) {
        await CreateOverdueEventsAsync(ct);
        // Recover outstanding requests created before request capture was introduced.
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO public.staff_notification_events(id,deduplication_key,event_type,order_id,created_at)
            SELECT gen_random_uuid(), 'package-request:' || r.id::text || ':REQUESTED',
                'PACKAGE_' || r.type || '_REQUESTED', r.order_id, r.created_at
            FROM public.package_change_requests r
            WHERE r.status = 'PENDING_APPROVAL' AND r.type IN ('PAUSE','RESUME')
                AND NOT EXISTS (SELECT 1 FROM public.staff_notification_events e
                    WHERE e.deduplication_key = 'package-request:' || r.id::text || ':REQUESTED')
            ON CONFLICT(deduplication_key) DO NOTHING
            """, ct);
        for(var batch=0;batch<10;batch++) {
            await using var tx=await db.Database.BeginTransactionAsync(ct);
            var events=await db.StaffNotificationEvents.FromSqlRaw("SELECT * FROM public.staff_notification_events WHERE processed_at IS NULL ORDER BY created_at,id LIMIT 100 FOR UPDATE SKIP LOCKED").ToArrayAsync(ct);
            if(events.Length==0) { await tx.CommitAsync(ct); return; }
            var recipients=await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id
                where r.Name=="Admin" select new { ur.UserId,Role=r.Name }).ToArrayAsync(ct);
            var ids=events.Select(e=>e.OrderId).Distinct().ToArray();
            var orders=await db.Orders.AsNoTracking().Where(o=>ids.Contains(o.Id)).ToDictionaryAsync(o=>o.Id,o=>o.OrderNumber,ct);
            foreach(var e in events) {
                if(orders.TryGetValue(e.OrderId,out var number)) {
                    var content=StaffNotificationRules.Content(e.EventType,number,e.ServiceDate);
                    foreach(var recipient in recipients.GroupBy(r=>r.UserId)) {
                        var id=Guid.NewGuid();
                        await db.Database.ExecuteSqlInterpolatedAsync($"""
                            INSERT INTO public.staff_notifications(id,recipient_user_id,deduplication_key,event_type,title,message,order_id,order_number,service_day_id,service_date,created_at)
                            VALUES ({id},{recipient.Key},{e.DeduplicationKey},{e.EventType},{content.Title},{content.Message},{e.OrderId},{number},{e.ServiceDayId},{e.ServiceDate},{e.CreatedAt})
                            ON CONFLICT(recipient_user_id,deduplication_key) DO NOTHING
                            """,ct);
                    }
                }
                e.ProcessedAt=clock.GetUtcNow();
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            db.ChangeTracker.Clear();
        }
    }

    private async Task CreateOverdueEventsAsync(CancellationToken ct) {
        var now=clock.GetUtcNow();
        var today=SubscriptionMenuRules.QatarToday(now);
        var eligibleOrders=db.Orders.Where(OrderWorkflowPolicy.FulfillmentFilter).Select(o=>o.Id);
        var days=await db.Set<SubscriptionServiceDay>().AsNoTracking().Include(d=>d.Order).ThenInclude(o=>o!.DeliveryDays)
            .Where(d=>d.OrderId!=null && eligibleOrders.Contains(d.OrderId.Value) && d.ServiceDate<=today &&
                (d.Status=="PLANNED" || d.Status=="CONFIRMED" || d.Status=="SCHEDULED") && d.Order!.FulfillmentMethod=="HOME_DELIVERY" &&
                !db.StaffNotificationEvents.Any(e=>e.ServiceDayId==d.Id && e.EventType=="DELIVERY_OVERDUE"))
            .AsSplitQuery().ToArrayAsync(ct);
        var slots=await db.DeliveryTimeSlots.AsNoTracking().ToDictionaryAsync(s=>s.Id,ct);
        foreach(var day in days) {
            var order=day.Order!;
            var deadline=DailyFulfillmentTracking.Deadline(day.ServiceDate,order,order.DeliveryTimeSlotId is { } slot ? slots.GetValueOrDefault(slot) : null);
            if(!DailyFulfillmentTracking.IsEligible(day,order) || !DailyFulfillmentTracking.IsOverdue(day.ServiceDate,day.Status,deadline,now)) continue;
            var id=Guid.NewGuid(); var key=$"overdue:{day.Id}";
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO public.staff_notification_events(id,deduplication_key,event_type,order_id,service_day_id,service_date,created_at)
                VALUES ({id},{key},'DELIVERY_OVERDUE',{order.Id},{day.Id},{day.ServiceDate},{now})
                ON CONFLICT(deduplication_key) DO NOTHING
                """,ct);
        }
    }
}
