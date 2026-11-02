using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Single_restart_date_accepts_tomorrow_and_preserves_package_duration()
    {
        await PrepareAsync();
        var service = Service();
        var tomorrow = PackagePauseRules.MinimumResumeDate(clock.Now);
        var current = await service.GetPackageAsync(order.Id, userId, default);
        var preview = await service.PreviewPauseAsync(order.Id, userId, new(tomorrow, tomorrow, true), default);
        var result = await service.PauseAsync(order.Id, userId, new(Guid.NewGuid(), preview.RowVersion, preview.ScheduleVersion, tomorrow, tomorrow, null, true), default);
        Assert.Equal(current.RemainingServiceDays, result.RemainingServiceDays);
        Assert.Equal(current.ExpectedEndDate, result.ExpectedEndDate);
        Assert.Equal(tomorrow, result.NextScheduledDelivery);
    }
    [UpcomingPostgresFact]
    public async Task Chosen_restart_beyond_original_end_preserves_days_and_replay_checks_date()
    {
        await PrepareAsync();
        var service = Service();
        var before = await db.Set<SubscriptionServiceDay>().AsNoTracking().Where(d => d.OrderId == order.Id).ToArrayAsync();
        var current = await service.GetPackageAsync(order.Id, userId, default);
        var start = current.EligiblePauseDates.First();
        var restart = current.ExpectedEndDate.AddMonths(2);
        var preview = await service.PreviewPauseAsync(order.Id, userId, new(start, restart), default);
        var pauseId = Guid.NewGuid();
        var paused = await service.PauseAsync(order.Id, userId, new(pauseId, preview.RowVersion, preview.ScheduleVersion, start, restart, null), default);
        Assert.True(paused.ExpectedEndDate > current.ExpectedEndDate);
        var chosen = PackagePauseRules.MinimumResumeDate(clock.Now);
        var request = new ResumePackageRequest(Guid.NewGuid(), pauseId, paused.RowVersion, chosen);
        var result = await service.ResumeAsync(order.Id, userId, request, default);
        Assert.Null(result.CurrentPause);
        var after = await db.Set<SubscriptionServiceDay>().AsNoTracking().Where(d => d.OrderId == order.Id).ToArrayAsync();
        Assert.Equal(before.Select(d => d.Id).Order(), after.Select(d => d.Id).Order());
        Assert.Equal(after.Max(d => d.ServiceDate), result.ExpectedEndDate);
        await service.ResumeAsync(order.Id, userId, request, default);
        var conflict = await Assert.ThrowsAsync<UpcomingMealException>(() => service.ResumeAsync(order.Id, userId, request with { ResumeDate = chosen.AddDays(1) }, default));
        Assert.Equal("idempotency_conflict", conflict.Code);
        var eligibleStart = (await service.GetPackageAsync(order.Id, userId, default)).EligiblePauseDates.First();
        var error = await Assert.ThrowsAsync<UpcomingMealException>(() => service.PreviewPauseAsync(order.Id, userId, new(eligibleStart, PackagePauseRules.MaximumResumeDate(clock.Now).AddDays(1)), default));
        Assert.Equal("invalid_resume_date", error.Code);
    }
    [UpcomingPostgresFact]
    public async Task Pause_rejects_today_past_and_locked_dates_at_preview_and_confirmation() {
        await PrepareAsync();
        clock.Now = new(2026,10,27,8,0,0,TimeSpan.Zero);
        foreach (var date in new[] { new DateOnly(2026,10,26), new DateOnly(2026,10,27) }) {
            var error = await Assert.ThrowsAsync<UpcomingMealException>(() => Service().PreviewPauseAsync(order.Id,userId,new(date,date.AddDays(3)),default));
            Assert.Equal("pause_date_unavailable", error.Code);
            await Assert.ThrowsAsync<UpcomingMealException>(() => Service().PauseAsync(order.Id,userId,new(Guid.NewGuid(),1,"",date,date.AddDays(3),null),default));
        }
        var day = await db.Set<SubscriptionServiceDay>().Where(d=>d.OrderId==order.Id && d.ServiceDate>new DateOnly(2026,10,27)).OrderBy(d=>d.ServiceDate).FirstAsync();
        day.SelectionLocked=true; await db.SaveChangesAsync();
        var package=await Service().GetPackageAsync(order.Id,userId,default);
        Assert.DoesNotContain(day.ServiceDate,package.EligiblePauseDates);
        Assert.True(package.EligiblePauseDates.First()>day.ServiceDate);
        await Assert.ThrowsAsync<UpcomingMealException>(()=>Service().PreviewPauseAsync(order.Id,userId,new(day.ServiceDate,day.ServiceDate.AddDays(3)),default));
    }

    [UpcomingPostgresFact]
    public async Task Pause_and_resume_preserve_entitlement_IDs_history_and_independent_orders() {
        await PrepareAsync();
        var before=await db.Set<SubscriptionServiceDay>().AsNoTracking().Where(d=>d.OrderId==order.Id).OrderBy(d=>d.ServiceDate).ToArrayAsync();
        var days=await db.Set<SubscriptionServiceDay>().Where(d=>d.OrderId==order.Id).OrderBy(d=>d.ServiceDate).ToArrayAsync();
        days[0].Status="DELIVERED";days[0].FulfilledAt=clock.Now;days[0].SelectionLocked=true;
        days[1].SelectionLocked=true;days[1].CustomerConfirmedAt=clock.Now;
        await db.SaveChangesAsync();
        var fixedDay=(days[0].Id,days[0].ServiceDate,days[0].Status,days[0].FulfilledAt);
        var otherId=Guid.NewGuid(); var other=new SubscriptionServiceDay { Id=otherId,SubscriptionId=Guid.NewGuid(),ServiceDate=days[2].ServiceDate,CreatedAt=clock.Now,UpdatedAt=clock.Now };
        db.Add(other);await db.SaveChangesAsync();
        var service=Service();var start=days[2].ServiceDate;var resume=start.AddDays(8);
        var preview=await service.PreviewPauseAsync(order.Id,userId,new(start,resume),default);
        var command=Guid.NewGuid();var request=new PausePackageRequest(command,preview.RowVersion,preview.ScheduleVersion,start,resume,"Travel");
        var result=await service.PauseAsync(order.Id,userId,request,default);
        Assert.Equal(before.Length-1,result.RemainingServiceDays);Assert.True(result.ExpectedEndDate>before.Last().ServiceDate);
        var after=await db.Set<SubscriptionServiceDay>().AsNoTracking().Where(d=>d.OrderId==order.Id).ToArrayAsync();
        Assert.Equal(before.Select(d=>d.Id).Order(),after.Select(d=>d.Id).Order());
        Assert.Equal(after.Length,after.Select(d=>d.ServiceDate).Distinct().Count());
        var fulfilled=after.Single(d=>d.Id==days[0].Id);Assert.Equal(fixedDay,(fulfilled.Id,fulfilled.ServiceDate,fulfilled.Status,fulfilled.FulfilledAt));
        Assert.Equal(days[1].ServiceDate,after.Single(d=>d.Id==days[1].Id).ServiceDate);
        Assert.All(after.Where(d=>d.Id!=days[0].Id && d.Id!=days[1].Id),d=>Assert.True(d.ServiceDate>=resume));
        Assert.Equal(other.ServiceDate,(await db.Set<SubscriptionServiceDay>().AsNoTracking().SingleAsync(d=>d.Id==otherId)).ServiceDate);
        var calendar=new AdminDeliveryCalendarService(db,Options.Create(new MenuRotationOptions()),clock);
        Assert.Equal(0,(await calendar.GetPreparationSummaryAsync(start,default)).OrderCount);
        Assert.Empty((await calendar.GetMonthAsync(start,start,null,null,default)).Days.Single().Orders);
        await service.PauseAsync(order.Id,userId,request,default);
        Assert.Single(await service.GetPausesAsync(order.Id,userId,default));
        await service.InitializeAsync(await db.Orders.Include(o=>o.Meals).Include(o=>o.DeliveryDays).SingleAsync(o=>o.Id==order.Id),default);
        Assert.Equal(before.Length,await db.Set<SubscriptionServiceDay>().CountAsync(d=>d.OrderId==order.Id));
        var resumeRequest=new ResumePackageRequest(Guid.NewGuid(),command,result.RowVersion);
        var early=await service.ResumeAsync(order.Id,userId,resumeRequest,default);
        Assert.True(early.ExpectedEndDate<result.ExpectedEndDate);
        await service.ResumeAsync(order.Id,userId,resumeRequest,default);
        // Original pause request remains replayable after Resume Early changes its effective resume date.
        await service.PauseAsync(order.Id,userId,request,default);
        Assert.Equal(before.Length,await db.Set<SubscriptionServiceDay>().CountAsync(d=>d.OrderId==order.Id));
        Assert.Equal(before.Length-1,early.RemainingServiceDays);
        Assert.Equal("PENDING",(await db.Orders.AsNoTracking().SingleAsync(o=>o.Id==order.Id)).PaymentStatus);
        var choices=await db.Set<CustomerMealSelection>().Where(s=>s.ServiceDay.OrderId==order.Id && s.Status!="CANCELLED").ToArrayAsync();
        Assert.Equal(choices.Length,choices.Select(s=>(s.SubscriptionServiceDayId,s.MenuItemId)).Distinct().Count());
        Assert.NotEmpty(await db.Set<CustomerMealSelectionHistory>().Where(h=>after.Select(d=>d.Id).Contains(h.SubscriptionServiceDayId)).ToListAsync());
    }

    [UpcomingPostgresFact]
    public async Task Confirmation_rechecks_changes_since_preview_and_rejects_other_owners() {
        await PrepareAsync();var service=Service();var management=await service.GetPackageAsync(order.Id,userId,default);
        var start=management.EligiblePauseDates[0];var preview=await service.PreviewPauseAsync(order.Id,userId,new(start,start.AddDays(4)),default);
        var day=await db.Set<SubscriptionServiceDay>().SingleAsync(d=>d.OrderId==order.Id && d.ServiceDate==start);
        day.SelectionLocked=true;day.Revision++;await db.SaveChangesAsync();
        var request=new PausePackageRequest(Guid.NewGuid(),preview.RowVersion,preview.ScheduleVersion,start,start.AddDays(4),null);
        await Assert.ThrowsAsync<UpcomingMealException>(()=>service.PauseAsync(order.Id,userId,request,default));
        Assert.Empty(await db.Set<SubscriptionPause>().ToArrayAsync());
        var next=await db.Set<SubscriptionServiceDay>().Where(d=>d.OrderId==order.Id && !d.SelectionLocked).OrderBy(d=>d.ServiceDate).FirstAsync();
        next.MealCount=null;await db.SaveChangesAsync();
        var invalidEntitlement=await Assert.ThrowsAsync<UpcomingMealException>(()=>service.PreviewPauseAsync(order.Id,userId,new(next.ServiceDate,next.ServiceDate.AddDays(4)),default));
        Assert.Equal("schedule_review_required",invalidEntitlement.Code);
        var error=await Assert.ThrowsAsync<UpcomingMealException>(()=>service.GetPackageAsync(order.Id,Guid.NewGuid(),default));Assert.Equal(404,error.StatusCode);
    }

    [UpcomingPostgresFact]
    public async Task Pause_confirmation_rechecks_cutoff_and_concurrent_retries_are_idempotent() {
        var menu=await PrepareAsync();var service=Service();var start=menu.DeliveryDate;
        var preview=await service.PreviewPauseAsync(order.Id,userId,new(start,start.AddDays(7)),default);
        var request=new PausePackageRequest(Guid.NewGuid(),preview.RowVersion,preview.ScheduleVersion,start,start.AddDays(7),null);
        clock.Now=menu.CutoffAt;
        await Assert.ThrowsAsync<UpcomingMealException>(()=>service.PauseAsync(order.Id,userId,request,default));
        Assert.Empty(await db.Set<SubscriptionPause>().ToArrayAsync());
        clock.Now=menu.CutoffAt.AddMinutes(-1);
        async Task<PackageManagementResponse> Attempt() { await using var fresh=Context();return await Service(fresh).PauseAsync(order.Id,userId,request,default); }
        var results=await Task.WhenAll(Attempt(),Attempt());
        Assert.Equal(results[0].RowVersion,results[1].RowVersion);
        Assert.Single(await db.Set<SubscriptionPause>().AsNoTracking().ToArrayAsync());
        Assert.Equal(12,await db.Set<SubscriptionServiceDay>().CountAsync(d=>d.OrderId==order.Id));
    }

    [UpcomingPostgresFact]
    public async Task Pause_rolls_back_parked_dates_if_a_later_schedule_write_fails() {
        await PrepareAsync();var service=Service();var before=await db.Set<SubscriptionServiceDay>().AsNoTracking().Where(d=>d.OrderId==order.Id).OrderBy(d=>d.Id).Select(d=>d.ServiceDate).ToArrayAsync();
        var management=await service.GetPackageAsync(order.Id,userId,default);var start=management.EligiblePauseDates.First();
        var preview=await service.PreviewPauseAsync(order.Id,userId,new(start,start.AddDays(7)),default);
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION test_pause_failure() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.end_date <> OLD.end_date THEN RAISE EXCEPTION 'injected pause failure'; END IF; RETURN NEW; END $$");
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER test_pause_failure BEFORE UPDATE ON public.orders FOR EACH ROW EXECUTE FUNCTION test_pause_failure()");
        await Assert.ThrowsAsync<DbUpdateException>(()=>service.PauseAsync(order.Id,userId,new(Guid.NewGuid(),preview.RowVersion,preview.ScheduleVersion,start,start.AddDays(7),null),default));
        await using var fresh=Context();
        Assert.Equal(before,await fresh.Set<SubscriptionServiceDay>().AsNoTracking().Where(d=>d.OrderId==order.Id).OrderBy(d=>d.Id).Select(d=>d.ServiceDate).ToArrayAsync());
        Assert.Empty(await fresh.Set<SubscriptionPause>().ToArrayAsync());
        Assert.Equal(management.RowVersion,(await fresh.Orders.SingleAsync(o=>o.Id==order.Id)).RowVersion);
    }

    [UpcomingPostgresFact]
    public async Task Pause_HTTP_endpoints_authenticate_scope_preview_replay_resume_and_return_actual_dates() {
        await PrepareAsync();
        await using var factory=new ApiFactory(connection, services=> {
            services.AddSingleton<TimeProvider>(clock);
            services.PostConfigure<DietTime.Infrastructure.TwilioWhatsAppOptions>(o=>o.Enabled=false);
        });
        using var client=factory.CreateClient();var path=$"/api/v1/orders/{order.Id}";
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync(path+"/package-management")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Development-User-Id",Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync(path+"/package-management")).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Development-User-Id");client.DefaultRequestHeaders.Add("X-Development-User-Id",userId.ToString());
        var management=await client.GetFromJsonAsync<PackageManagementResponse>(path+"/package-management");
        var start=management!.EligiblePauseDates.First();var resume=start.AddDays(8);
        var previewResponse=await client.PostAsJsonAsync(path+"/pause/preview",new PausePreviewRequest(start,resume));
        Assert.Equal(HttpStatusCode.OK,previewResponse.StatusCode);
        var preview=(await previewResponse.Content.ReadFromJsonAsync<PausePreviewResponse>())!;
        Assert.Empty(await db.Set<SubscriptionPause>().ToArrayAsync());
        var request=new PausePackageRequest(Guid.NewGuid(),preview.RowVersion,preview.ScheduleVersion,start,resume,"Travel");
        var confirm=await client.PostAsJsonAsync(path+"/pause",request);Assert.Equal(HttpStatusCode.OK,confirm.StatusCode);
        var paused=(await confirm.Content.ReadFromJsonAsync<PackageManagementResponse>())!;
        Assert.Equal(12,paused.RemainingServiceDays);Assert.NotNull(paused.CurrentPause);
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync(path+"/pause",request)).StatusCode);
        Assert.Single((await client.GetFromJsonAsync<PackagePauseResponse[]>(path+"/pauses"))!);
        var resumeRequest=new ResumePackageRequest(Guid.NewGuid(),request.CommandId,paused.RowVersion);
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync(path+"/resume",resumeRequest)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync(path+"/resume",resumeRequest)).StatusCode);
        var details=(await client.GetFromJsonAsync<PlaceOrderResponse>(path))!;
        var actualDates=await db.Set<SubscriptionServiceDay>().AsNoTracking().Where(d=>d.OrderId==order.Id).OrderBy(d=>d.ServiceDate).Select(d=>d.ServiceDate).ToArrayAsync();
        Assert.Equal(actualDates,details.Delivery.ServiceDates);
        Assert.Equal("PENDING",details.PaymentStatus);
    }
}
