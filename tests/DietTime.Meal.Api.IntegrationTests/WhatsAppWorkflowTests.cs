using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DietTime.Application;
using DietTime.Domain;
using DietTime.Infrastructure;
using DietTime.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System.Security.Cryptography;
using System.Text;

namespace DietTime.Meal.Api.IntegrationTests;
public sealed partial class UpcomingMealServiceTests
{
    private const string WhatsAppPublicUrl = "https://diettime.test";
    private const string WhatsAppTestToken = "test-webhook-token";
    private static readonly string WhatsAppAccount = "AC" + new string('1',32);
    private static readonly string WhatsAppContent = "HX" + new string('2',32);
    private async Task<(ApiFactory Factory, HttpClient Client, DietTime.Contracts.PlaceOrderRequest Request, RecordingWhatsApp Provider)> WhatsAppClient()
    {
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.PhoneNumber, "+97455555555"));
        var role = new IdentityRole<Guid> { Id=Guid.NewGuid(), Name="Admin", NormalizedName="ADMIN" };
        db.Roles.Add(role); db.UserRoles.Add(new IdentityUserRole<Guid> { UserId=userId,RoleId=role.Id }); await db.SaveChangesAsync();
        var provider = new RecordingWhatsApp();
        var setup = await ApprovalClient(configure: services => {
            services.RemoveAll<IHostedService>();
            services.AddSingleton<ITwilioWhatsAppService>(provider);
            services.PostConfigure<TwilioWhatsAppOptions>(o => {
                o.Enabled=true;o.AdminActionsEnabled=true;o.AccountSid=WhatsAppAccount;o.AuthToken=WhatsAppTestToken;o.FromNumber="+14155238886";
                o.OperationsNumber="+97477777777";o.NewOrderContentSid=WhatsAppContent;o.PublicWebhookBaseUrl=WhatsAppPublicUrl;
                o.AuthorizedAdmins=[new() { Phone="+97477777777",UserId=userId }];
            });
            services.PostConfigure<OrderWhatsAppTemplateOptions>(o => {
                o.ApprovedContentSid=WhatsAppContent;o.PaymentReceivedContentSid=WhatsAppContent;o.CancelledContentSid=WhatsAppContent;
                o.PausedContentSid=WhatsAppContent;o.ResumedContentSid=WhatsAppContent;o.MealReminderContentSid=WhatsAppContent;
            });
        });
        return(setup.Factory,setup.Client,setup.Request,provider);
    }
    private static async Task DispatchWhatsApp(ApiFactory factory, Guid? orderId=null)
    {
        await using var scope=factory.Services.CreateAsyncScope();
        var sender=scope.ServiceProvider.GetRequiredService<IOrderNotificationSender>();
        if(orderId.HasValue)await sender.SendAsync(orderId.Value,default);else await sender.SendPendingAsync(default);
    }
    private static async Task<HttpResponseMessage> SignedWhatsApp(HttpClient client,string path,Dictionary<string,string> fields,bool valid=true)
    {
        fields["AccountSid"]=WhatsAppAccount;
        using var request=new HttpRequestMessage(HttpMethod.Post,path){Content=new FormUrlEncodedContent(fields)};
        request.Headers.Add("X-Twilio-Signature",valid ? Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(WhatsAppTestToken), Encoding.UTF8.GetBytes(WhatsAppPublicUrl+path+string.Concat(fields.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>p.Key+p.Value))))) : "invalid");
        return await client.SendAsync(request);
    }
    private static Dictionary<string,string> Reply(string original,string action="approve",string? sid=null) => new() {
        ["MessageSid"]=sid??"SM"+Guid.NewGuid().ToString("N"),["OriginalRepliedMessageSid"]=original,
        ["From"]="whatsapp:+97477777777",["To"]="whatsapp:+14155238886",["ButtonPayload"]="diettime:"+action,["Body"]=action };

    [UpcomingPostgresFact]
    public async Task WhatsApp_order_outbox_uses_customer_phone_and_saved_takeaway_or_home_snapshot()
    {
        var setup=await WhatsAppClient();await using var factory=setup.Factory;using var client=setup.Client;
        await db.TakeawayBranches.Where(b=>b.Id==setup.Request.TakeawayBranchId).ExecuteUpdateAsync(s=>s.SetProperty(b=>b.Address,"Azizia street"));
        var id=(await SubmitApprovalOrder(client,setup.Request)).GetProperty("id").GetGuid();
        Assert.Empty(setup.Provider.Messages);Assert.Equal("PENDING",(await db.Orders.AsNoTracking().SingleAsync(o=>o.Id==id)).PaymentStatus);
        await DispatchWhatsApp(factory,id);
        var sent=Assert.Single(setup.Provider.Messages);
        Assert.Equal("+97477777777",sent.To);Assert.Equal("+97455555555",sent.ContentVariables["3"]);
        Assert.Equal("Takeaway",sent.ContentVariables["8"]);Assert.Contains("Azizia street",sent.ContentVariables["9"]);Assert.Equal(10,sent.ContentVariables.Count);
        var record=await db.OrderNotifications.AsNoTracking().SingleAsync(n=>n.OrderId==id&&n.Audience=="STAFF");
        Assert.NotNull(record.MessageSid);Assert.Equal("diet_time_new_order_admin",record.TemplateName);Assert.Equal("SENT",record.Status);
        await DispatchWhatsApp(factory,id);Assert.Single(setup.Provider.Messages);
        var slot=new DeliveryTimeSlot { Id=Guid.NewGuid(),Code="WA",Name="Morning",NameAr="Morning",StartTime=new(7,0),EndTime=new(11,0),IsActive=true };
        var address=new CustomerAddress { Id=Guid.NewGuid(),CustomerProfileId=order.CustomerProfileId,AddressType="HOME",Area="Doha",BuildingNo="10",StreetNo="20",ZoneNo="30",IsActive=true };
        db.AddRange(slot,address);await db.SaveChangesAsync();
        var home=setup.Request with { FulfillmentMethod="HOME_DELIVERY",TakeawayBranchId=null,CustomerAddressId=address.Id,DeliveryTimeSlotId=slot.Id };
        var homeId=(await SubmitApprovalOrder(client,home)).GetProperty("id").GetGuid();
        address.BuildingNo="999";await db.SaveChangesAsync();await DispatchWhatsApp(factory,homeId);
        var homeMessage=setup.Provider.Messages.Last();Assert.Contains("Building 10",homeMessage.ContentVariables["9"]);Assert.DoesNotContain("999",homeMessage.ContentVariables["9"]);
        Assert.Equal("Home delivery",homeMessage.ContentVariables["8"]);
    }
    [UpcomingPostgresFact]
    public async Task WhatsApp_verified_admin_replies_use_mapping_are_replay_safe_and_never_record_payment()
    {
        var setup=await WhatsAppClient();await using var factory=setup.Factory;using var client=setup.Client;
        foreach(var action in new[]{"approve","reject"}) {
            var id=(await SubmitApprovalOrder(client,setup.Request)).GetProperty("id").GetGuid();await DispatchWhatsApp(factory,id);
            var mapping=await db.OrderNotifications.AsNoTracking().SingleAsync(n=>n.OrderId==id&&n.Audience=="STAFF");
            var fields=Reply(mapping.MessageSid!,action);
            Assert.Equal(HttpStatusCode.Forbidden,(await SignedWhatsApp(client,"/api/webhooks/twilio/whatsapp",fields,false)).StatusCode);
            var stranger=Reply(mapping.MessageSid!,action);stranger["From"]="whatsapp:+97411111111";
            Assert.Equal(HttpStatusCode.Forbidden,(await SignedWhatsApp(client,"/api/webhooks/twilio/whatsapp",stranger)).StatusCode);
            var textOnly=Reply(mapping.MessageSid!,action);textOnly.Remove("ButtonPayload");
            await SignedWhatsApp(client,"/api/webhooks/twilio/whatsapp",textOnly);
            Assert.Equal("PENDING_APPROVAL",(await db.Orders.AsNoTracking().SingleAsync(o=>o.Id==id)).Status);
            Assert.Equal(HttpStatusCode.OK,(await SignedWhatsApp(client,"/api/webhooks/twilio/whatsapp",fields)).StatusCode);
            Assert.Equal(HttpStatusCode.OK,(await SignedWhatsApp(client,"/api/webhooks/twilio/whatsapp",fields)).StatusCode);
            var saved=await db.Orders.AsNoTracking().SingleAsync(o=>o.Id==id);
            Assert.Equal(action=="approve"?"CONFIRMED":"REJECTED",saved.Status);Assert.Equal("PENDING",saved.PaymentStatus);Assert.Equal(2,saved.RowVersion);
            Assert.Single(await db.Set<WhatsAppWebhookReceipt>().Where(w=>w.OrderId==id).ToArrayAsync());
            Assert.Single(await db.OrderStatusHistory.Where(h=>h.OrderId==id&&h.PreviousStatus=="PENDING_APPROVAL").ToArrayAsync());
        }
        await DispatchWhatsApp(factory);
        Assert.Contains(setup.Provider.Messages,m=>m.ContentVariables.Count==7);
    }
    [UpcomingPostgresFact]
    public async Task WhatsApp_callbacks_verify_signature_and_handle_early_repeated_and_out_of_order_delivery()
    {
        var setup=await WhatsAppClient();await using var factory=setup.Factory;using var client=setup.Client;
        var id=(await SubmitApprovalOrder(client,setup.Request)).GetProperty("id").GetGuid();await DispatchWhatsApp(factory,id);
        var message=await db.OrderNotifications.AsNoTracking().SingleAsync(n=>n.OrderId==id&&n.Audience=="STAFF");
        var path=$"/api/webhooks/twilio/whatsapp/status?notificationId={message.Id}";
        var fields=new Dictionary<string,string>{["MessageSid"]=message.MessageSid!,["To"]="whatsapp:+97477777777",["MessageStatus"]="read"};
        Assert.Equal(HttpStatusCode.Forbidden,(await SignedWhatsApp(client,path,fields,false)).StatusCode);
        foreach(var status in new[]{"read","sent","delivered","failed","read"}) {
            fields["MessageStatus"]=status;Assert.Equal(HttpStatusCode.NoContent,(await SignedWhatsApp(client,path,fields)).StatusCode);
        }
        var saved=await db.OrderNotifications.AsNoTracking().SingleAsync(n=>n.Id==message.Id);
        Assert.Equal("READ",saved.Status);Assert.NotNull(saved.ReadAt);Assert.NotNull(saved.DeliveredAt);
        var history=await client.GetFromJsonAsync<JsonElement[]>($"/api/admin/orders/{id}/whatsapp-notifications");
        Assert.Contains(history!,h=>h.GetProperty("status").GetString()=="READ");
        // Simulate callback arriving before the HTTP send response was persisted.
        await db.OrderNotifications.Where(n=>n.Id==message.Id).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.MessageSid,(string?)null).SetProperty(n=>n.Status,"PROCESSING"));
        Assert.Equal(HttpStatusCode.NoContent,(await SignedWhatsApp(client,path,fields)).StatusCode);
        Assert.Equal(message.MessageSid,(await db.OrderNotifications.AsNoTracking().SingleAsync(n=>n.Id==message.Id)).MessageSid);
    }
    [UpcomingPostgresFact]
    public async Task WhatsApp_dashboard_confirmation_payment_and_cancellation_create_only_committed_events()
    {
        var setup=await WhatsAppClient();await using var factory=setup.Factory;using var client=setup.Client;
        var id=(await SubmitApprovalOrder(client,setup.Request)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK,(await Review(client,id,"approve",1)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await Review(client,id,"approve",2)).StatusCode);
        await client.PostAsJsonAsync($"/api/admin/orders/{id}/payment",new DietTime.Contracts.RecordOrderPaymentRequest(2,"PAID","Receipt verified"));
        await DispatchWhatsApp(factory,id);
        Assert.Single(await db.OrderNotifications.Where(n=>n.OrderId==id&&n.EventCode=="ORDER_APPROVED").ToArrayAsync());
        Assert.Contains(setup.Provider.Messages,m=>m.ContentVariables.Count==5&&m.ContentVariables["4"].StartsWith("QAR"));
        Assert.Equal(HttpStatusCode.OK,(await Review(client,id,"cancel",3)).StatusCode);
        await DispatchWhatsApp(factory,id);
        Assert.Equal("CANCELLED",(await db.Orders.AsNoTracking().SingleAsync(o=>o.Id==id)).Status);
        Assert.All(await db.Set<SubscriptionServiceDay>().Where(d=>d.OrderId==id).ToArrayAsync(),d=>Assert.Equal("CANCELLED",d.Status));
        Assert.Single(await db.OrderNotifications.Where(n=>n.OrderId==id&&n.EventCode=="ORDER_CANCELLED").ToArrayAsync());
        Assert.Contains(setup.Provider.Messages,m=>m.ContentVariables.Count==5&&m.ContentVariables["5"]=="Reason not provided");
    }
    [UpcomingPostgresFact]
    public async Task WhatsApp_pause_preview_is_silent_and_scheduled_or_early_resumption_sends_once_when_due()
    {
        var setup=await WhatsAppClient();await using var factory=setup.Factory;using var client=setup.Client;
        await PrepareAsync();
        var management=await Service().GetPackageAsync(order.Id,userId,default);var start=management.EligiblePauseDates.First();
        var preview=await Service().PreviewPauseAsync(order.Id,userId,new(start,start.AddDays(8),true),default);
        Assert.Empty(await db.OrderNotifications.ToArrayAsync());
        var command=new DietTime.Contracts.PausePackageRequest(Guid.NewGuid(),preview.RowVersion,preview.ScheduleVersion,start,start.AddDays(8),"Travel",true);
        await Service().PauseAsync(order.Id,userId,command,default);
        await Service().PauseAsync(order.Id,userId,command,default);
        Assert.Single(await db.OrderNotifications.Where(n=>n.EventCode=="PACKAGE_PAUSED").ToArrayAsync());
        await DispatchWhatsApp(factory,order.Id);
        Assert.Single(setup.Provider.Messages);
        clock.Now=new DateTimeOffset(start.AddDays(1).ToDateTime(TimeOnly.MinValue),TimeSpan.FromHours(3)).ToUniversalTime();
        var current=await Service().GetPackageAsync(order.Id,userId,default);
        await Service().ResumeAsync(order.Id,userId,new(Guid.NewGuid(),command.CommandId,current.RowVersion,start.AddDays(3)),default);
        await DispatchWhatsApp(factory,order.Id);Assert.Single(setup.Provider.Messages);
        var resume=await db.OrderNotifications.AsNoTracking().SingleAsync(n=>n.EventCode=="PACKAGE_RESUMED");
        clock.Now=resume.AvailableAt;await DispatchWhatsApp(factory,order.Id);await DispatchWhatsApp(factory,order.Id);
        Assert.Equal(2,setup.Provider.Messages.Count);Assert.Equal("SENT",(await db.OrderNotifications.AsNoTracking().SingleAsync(n=>n.Id==resume.Id)).Status);
    }
    [UpcomingPostgresFact]
    public async Task WhatsApp_retries_transient_failures_but_not_permanent_or_ambiguous_sends()
    {
        var setup=await WhatsAppClient();await using var factory=setup.Factory;using var client=setup.Client;
        var id=(await SubmitApprovalOrder(client,setup.Request)).GetProperty("id").GetGuid();
        setup.Provider.Next=new(){ErrorCode="503",ProviderHttpStatusCode=503};await DispatchWhatsApp(factory,id);
        var pending=await db.OrderNotifications.AsNoTracking().SingleAsync(n=>n.OrderId==id&&n.Audience=="STAFF");
        Assert.Equal("PENDING",pending.Status);Assert.Equal(1,pending.Attempts);
        clock.Now=pending.AvailableAt;await DispatchWhatsApp(factory,id);Assert.Equal(2,setup.Provider.Messages.Count);
        Assert.Equal("PENDING_APPROVAL",(await db.Orders.AsNoTracking().SingleAsync(o=>o.Id==id)).Status);
        var other=(await SubmitApprovalOrder(client,setup.Request)).GetProperty("id").GetGuid();
        setup.Provider.Next=new(){ErrorCode="timeout"};await DispatchWhatsApp(factory,other);
        Assert.Equal("UNKNOWN",(await db.OrderNotifications.AsNoTracking().SingleAsync(n=>n.OrderId==other&&n.Audience=="STAFF")).Status);
        clock.Now=clock.Now.AddHours(1);await DispatchWhatsApp(factory,other);Assert.Equal(3,setup.Provider.Messages.Count);
        var invalid=(await SubmitApprovalOrder(client,setup.Request)).GetProperty("id").GetGuid();
        setup.Provider.Next=new(){ErrorCode="21656",ProviderHttpStatusCode=400};await DispatchWhatsApp(factory,invalid);
        Assert.Equal("FAILED",(await db.OrderNotifications.AsNoTracking().SingleAsync(n=>n.OrderId==invalid&&n.Audience=="STAFF")).Status);
    }
    [UpcomingPostgresFact]
    public async Task WhatsApp_reminders_default_off_deduplicate_customer_date_and_skip_completed_selections()
    {
        var setup=await WhatsAppClient();await using var factory=setup.Factory;using var client=setup.Client;
        await PrepareAsync();var day=await db.Set<SubscriptionServiceDay>().OrderBy(d=>d.ServiceDate).FirstAsync();
        clock.Now=(day.SelectionCutoffAt??SubscriptionMenuRules.TomorrowCutoff(day.ServiceDate)).AddMinutes(-60);
        var options=new OrderWhatsAppTemplateOptions();var scheduler=new WhatsAppNotificationSchedule(db,clock,Microsoft.Extensions.Options.Options.Create(options));
        await scheduler.CreateRemindersAsync(default);Assert.Empty(await db.OrderNotifications.ToArrayAsync());
        options.MealRemindersEnabled=true;
        await scheduler.CreateRemindersAsync(default);await scheduler.CreateRemindersAsync(default);
        Assert.Single(await db.OrderNotifications.Where(n=>n.EventCode=="MEAL_REMINDER").ToArrayAsync());
        factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<OrderWhatsAppTemplateOptions>>().Value.MealRemindersEnabled=true;
        day.SelectionStatus="COMPLETED";await db.SaveChangesAsync();
        await DispatchWhatsApp(factory,order.Id);Assert.Empty(setup.Provider.Messages);
    }
    [UpcomingPostgresFact]
    public async Task WhatsApp_missing_configuration_is_retained_without_provider_attempts_and_recovers_after_configuration()
    {
        var setup=await WhatsAppClient();await using var factory=setup.Factory;using var client=setup.Client;
        var config=factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<TwilioWhatsAppOptions>>().Value;
        config.NewOrderContentSid="";
        var id=(await SubmitApprovalOrder(client,setup.Request)).GetProperty("id").GetGuid();await DispatchWhatsApp(factory,id);
        var pending=await db.OrderNotifications.AsNoTracking().SingleAsync(n=>n.OrderId==id&&n.Audience=="STAFF");
        Assert.Equal("WAITING_CONFIGURATION",pending.Status);Assert.Equal(0,pending.Attempts);Assert.Empty(setup.Provider.Messages);
        config.NewOrderContentSid=WhatsAppContent;clock.Now=pending.AvailableAt;await DispatchWhatsApp(factory,id);
        Assert.Single(setup.Provider.Messages);Assert.Equal("PENDING_APPROVAL",(await db.Orders.AsNoTracking().SingleAsync(o=>o.Id==id)).Status);
    }
    [UpcomingPostgresFact]
    public async Task WhatsApp_migration_is_additive_idempotent_and_preserves_history()
    {
        var migration=new DietTime.Persistence.Migrations.WhatsAppNotifications();
        var sql=migration.UpOperations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>().Single().Sql;
        var setup=await WhatsAppClient();await using var factory=setup.Factory;using var client=setup.Client;
        var id=(await SubmitApprovalOrder(client,setup.Request)).GetProperty("id").GetGuid();
        var before=await db.OrderNotifications.CountAsync();
        await using var migrationConnection=new Npgsql.NpgsqlConnection(connection);
        await migrationConnection.OpenAsync();
        // Recreate the pre-update shape while retaining the existing order/history rows.
        await using(var legacy=new Npgsql.NpgsqlCommand("""
            DROP TABLE public.whatsapp_webhook_receipts;
            ALTER TABLE public.order_notifications
              DROP COLUMN event_key, DROP COLUMN payload_json, DROP COLUMN recipient_phone,
              DROP COLUMN template_name, DROP COLUMN content_sid, DROP COLUMN provider_status,
              DROP COLUMN delivered_at, DROP COLUMN read_at;
            ALTER TABLE public.order_notifications ALTER COLUMN history_id SET NOT NULL;
            """,migrationConnection)) await legacy.ExecuteNonQueryAsync();
        for(var i=0;i<2;i++) {
            await using var command=new Npgsql.NpgsqlCommand("BEGIN;\n"+sql+"\nCOMMIT;",migrationConnection);
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(before,await db.OrderNotifications.CountAsync());
        await DispatchWhatsApp(factory,id);Assert.Single(setup.Provider.Messages);
    }
    [UpcomingPostgresFact]
    public async Task WhatsApp_scheduled_resume_and_enabled_reminder_send_once_at_their_actual_dates()
    {
        var setup=await WhatsAppClient();await using var factory=setup.Factory;using var client=setup.Client;
        await PrepareAsync();
        var data=await Service().GetPackageAsync(order.Id,userId,default);var start=data.EligiblePauseDates.First();
        var preview=await Service().PreviewPauseAsync(order.Id,userId,new(start,start.AddDays(8),true),default);
        await Service().PauseAsync(order.Id,userId,new(Guid.NewGuid(),preview.RowVersion,preview.ScheduleVersion,start,start.AddDays(8),null,true),default);
        await DispatchWhatsApp(factory,order.Id);Assert.Single(setup.Provider.Messages);
        var resume=await db.OrderNotifications.AsNoTracking().SingleAsync(n=>n.EventCode=="PACKAGE_RESUMED");
        clock.Now=resume.AvailableAt.AddSeconds(-1);await DispatchWhatsApp(factory,order.Id);Assert.Single(setup.Provider.Messages);
        clock.Now=resume.AvailableAt;await DispatchWhatsApp(factory,order.Id);await DispatchWhatsApp(factory,order.Id);Assert.Equal(2,setup.Provider.Messages.Count);
        Assert.Equal("PACKAGE_RESUMED",(await db.OrderNotifications.AsNoTracking().SingleAsync(n=>n.Id==resume.Id)).EventCode);
        var day=await db.Set<SubscriptionServiceDay>().AsNoTracking().Where(d=>d.OrderId==order.Id && d.ServiceDate > SubscriptionMenuRules.QatarToday(clock.Now)).OrderBy(d=>d.ServiceDate).FirstAsync();
        clock.Now=(day.SelectionCutoffAt??SubscriptionMenuRules.TomorrowCutoff(day.ServiceDate)).AddMinutes(-60);
        factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<OrderWhatsAppTemplateOptions>>().Value.MealRemindersEnabled=true;
        await DispatchWhatsApp(factory);await DispatchWhatsApp(factory);
        Assert.Equal(3,setup.Provider.Messages.Count);
        Assert.Single(await db.OrderNotifications.Where(n=>n.EventCode=="MEAL_REMINDER").ToArrayAsync());
        clock.Now=clock.Now.AddMinutes(61);await DispatchWhatsApp(factory);Assert.Equal(3,setup.Provider.Messages.Count);
    }
    private sealed class RecordingWhatsApp : ITwilioWhatsAppService
    {
        public List<TwilioWhatsAppTemplateMessage> Messages { get; }=[];
        public WhatsAppSendResult? Next;
        public Task<WhatsAppSendResult> SendTemplateAsync(TwilioWhatsAppTemplateMessage message,CancellationToken cancellationToken=default) {
            Messages.Add(message);var result=Next??new WhatsAppSendResult { Success=true,MessageId="SM"+Guid.NewGuid().ToString("N"),Status="queued" };Next=null;return Task.FromResult(result);
        }
        public Task<WhatsAppSendResult> SendNewOrderNotificationAsync(NewOrderWhatsAppNotification message,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public Task<WhatsAppMessageStatusResult> GetMessageStatusAsync(string sid,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
    }
}
