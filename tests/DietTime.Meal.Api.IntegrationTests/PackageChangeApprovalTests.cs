using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    private async Task<PausePackageRequest> ApprovalPauseCommand()
    {
        await EnforceManagedServiceDayConstraintAsync();
        await PrepareAsync();
        var start = (await Service().GetPackageAsync(order.Id, userId, default)).EligiblePauseDates.First();
        var preview = await Service().PreviewPauseAsync(order.Id, userId, new(start, start.AddDays(9), true), default);
        return new(Guid.NewGuid(), preview.RowVersion, preview.ScheduleVersion, start, start.AddDays(9), "Travel", true);
    }
    private Task<DateOnly[]> ApprovalSchedule(DietTime.Persistence.DietTimeDbContext context) => context.Set<SubscriptionServiceDay>()
        .AsNoTracking().Where(d => d.OrderId == order.Id).OrderBy(d => d.Id).Select(d => d.ServiceDate).ToArrayAsync();

    [UpcomingPostgresFact]
    public async Task Package_approval_resume_rechecks_committed_deliveries_while_waiting()
    {
        var command = await ApprovalPauseCommand();
        var paused = await Service().PauseAsync(order.Id,userId,command,default);
        var request = new ResumePackageRequest(Guid.NewGuid(),command.CommandId,paused.RowVersion,command.PauseStartDate);
        await Service().RequestResumeAsync(order.Id,userId,request,default);
        var before = await ApprovalSchedule(db);
        var day = await db.Set<SubscriptionServiceDay>().Where(d=>d.OrderId==order.Id).OrderBy(d=>d.ServiceDate).FirstAsync();
        day.SelectionLocked=true;day.CustomerConfirmedAt=clock.Now;await db.SaveChangesAsync();
        var error=await Assert.ThrowsAsync<UpcomingMealException>(()=>Service().ReviewChangeRequestAsync(request.CommandId,Guid.NewGuid(),new(true),default));
        Assert.Equal("schedule_changed",error.Code);
        await using var fresh=Context();
        Assert.Equal(before,await ApprovalSchedule(fresh));
        Assert.Equal("CONFIRMED",(await fresh.Set<SubscriptionPause>().SingleAsync()).Status);
        Assert.Equal("PENDING_APPROVAL",(await fresh.Set<PackageChangeRequest>().SingleAsync()).Status);
        Assert.True((await fresh.Set<SubscriptionServiceDay>().SingleAsync(d=>d.Id==day.Id)).SelectionLocked);
    }
    [UpcomingPostgresFact]
    public async Task Package_approval_pause_and_resume_wait_for_review_and_rejection_preserves_schedule()
    {
        var command = await ApprovalPauseCommand(); var original = await ApprovalSchedule(db); var service = Service();
        var pending = await service.RequestPauseAsync(order.Id, userId, command, default);
        Assert.Equal("PENDING_APPROVAL",pending.LatestRequest!.Status); Assert.Null(pending.CurrentPause);
        Assert.Equal(original,await ApprovalSchedule(db)); Assert.Empty(await db.Set<SubscriptionPause>().ToArrayAsync());
        Assert.Equal(command.ExpectedRowVersion,pending.RowVersion); Assert.Empty(pending.EligiblePauseDates);
        Assert.Equal("CONFIRMED",(await db.Orders.AsNoTracking().SingleAsync()).Status);
        await service.RequestPauseAsync(order.Id,userId,command,default);
        var duplicate=await Assert.ThrowsAsync<UpcomingMealException>(()=>service.RequestPauseAsync(order.Id,userId,command with {CommandId=Guid.NewGuid()},default));
        Assert.Equal("package_request_pending",duplicate.Code);
        Assert.Single((await service.ListChangeRequestsAsync(1,20,default)).Items);
        var reviewer=Guid.NewGuid();
        var missing=await Assert.ThrowsAsync<UpcomingMealException>(()=>service.ReviewChangeRequestAsync(command.CommandId,reviewer,new(false),default));
        Assert.Equal("review_reason_required",missing.Code);
        await service.ReviewChangeRequestAsync(command.CommandId,reviewer,new(false,"Please choose different dates"),default);
        Assert.Equal(original,await ApprovalSchedule(db));
        Assert.Equal("REJECTED",(await service.GetPackageAsync(order.Id,userId,default)).LatestRequest!.Status);
        command=command with {CommandId=Guid.NewGuid()}; clock.Now=clock.Now.AddSeconds(1);
        await service.RequestPauseAsync(order.Id,userId,command,default);
        await service.ReviewChangeRequestAsync(command.CommandId,reviewer,new(true),default);
        var paused=await service.GetPackageAsync(order.Id,userId,default);
        Assert.NotNull(paused.CurrentPause); Assert.Equal("APPROVED",paused.LatestRequest!.Status);
        Assert.Equal(12,paused.RemainingServiceDays); var pausedDates=await ApprovalSchedule(db); Assert.NotEqual(original,pausedDates);
        var resume=new ResumePackageRequest(Guid.NewGuid(),command.CommandId,paused.RowVersion,command.PauseStartDate);
        var release=await service.RequestResumeAsync(order.Id,userId,resume,default);
        Assert.Equal("RESUME",release.LatestRequest!.Type); Assert.Equal("PENDING_APPROVAL",release.LatestRequest.Status);
        Assert.NotNull(release.CurrentPause); Assert.False(release.CanResumeEarly); Assert.Equal(pausedDates,await ApprovalSchedule(db));
        await service.ReviewChangeRequestAsync(resume.CommandId,reviewer,new(false,"Resume date needs review"),default);
        Assert.Equal(pausedDates,await ApprovalSchedule(db)); Assert.NotNull((await service.GetPackageAsync(order.Id,userId,default)).CurrentPause);
        clock.Now=clock.Now.AddSeconds(1); resume=resume with {CommandId=Guid.NewGuid()};
        await service.RequestResumeAsync(order.Id,userId,resume,default);
        await service.ReviewChangeRequestAsync(resume.CommandId,reviewer,new(true),default);
        Assert.Null((await service.GetPackageAsync(order.Id,userId,default)).CurrentPause);
        Assert.Equal(original,await ApprovalSchedule(db)); Assert.Empty((await service.ListChangeRequestsAsync(1,20,default)).Items);
        Assert.All(await db.Set<PackageChangeRequest>().AsNoTracking().Where(r=>r.Status!="PENDING_APPROVAL").ToArrayAsync(),r=>{
            Assert.Equal(reviewer,r.ReviewedBy);Assert.NotNull(r.ReviewedAt);
        });
    }

    [UpcomingPostgresFact]
    public async Task Package_approval_concurrent_submit_and_accept_are_idempotent_and_ownership_is_enforced()
    {
        var command=await ApprovalPauseCommand();
        await Assert.ThrowsAsync<UpcomingMealException>(()=>Service().RequestPauseAsync(order.Id,Guid.NewGuid(),command,default));
        async Task Submit(){await using var fresh=Context();await Service(fresh).RequestPauseAsync(order.Id,userId,command,default);}
        await Task.WhenAll(Submit(),Submit());
        Assert.Single(await db.Set<PackageChangeRequest>().AsNoTracking().ToArrayAsync());
        var reviewer=Guid.NewGuid();
        async Task Accept(){await using var fresh=Context();await Service(fresh).ReviewChangeRequestAsync(command.CommandId,reviewer,new(true),default);}
        await Task.WhenAll(Accept(),Accept());
        Assert.Single(await db.Set<SubscriptionPause>().AsNoTracking().ToArrayAsync());
        Assert.Equal(command.ExpectedRowVersion+1,(await db.Orders.AsNoTracking().SingleAsync()).RowVersion);
        var conflict=await Assert.ThrowsAsync<UpcomingMealException>(()=>Service().ReviewChangeRequestAsync(command.CommandId,reviewer,new(false,"Changed decision"),default));
        Assert.Equal("request_already_reviewed",conflict.Code);
    }

    [UpcomingPostgresFact]
    public async Task Package_approval_rechecks_cutoff_and_rolls_back_if_decision_save_fails()
    {
        var command=await ApprovalPauseCommand();var original=await ApprovalSchedule(db);
        await Service().RequestPauseAsync(order.Id,userId,command,default);
        clock.Now=SubscriptionMenuRules.TomorrowCutoff(command.PauseStartDate);
        await Assert.ThrowsAsync<UpcomingMealException>(()=>Service().ReviewChangeRequestAsync(command.CommandId,Guid.NewGuid(),new(true),default));
        await using(var fresh=Context()){
            Assert.Equal(original,await ApprovalSchedule(fresh)); Assert.Empty(await fresh.Set<SubscriptionPause>().ToArrayAsync());
            Assert.Equal("PENDING_APPROVAL",(await fresh.Set<PackageChangeRequest>().SingleAsync()).Status);
        }
        clock.Now=clock.Now.AddHours(-1);
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION test_package_review_failure() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.status = 'APPROVED' THEN RAISE EXCEPTION 'injected decision failure'; END IF; RETURN NEW; END $$");
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER test_package_review_failure BEFORE UPDATE ON public.package_change_requests FOR EACH ROW EXECUTE FUNCTION test_package_review_failure()");
        await using(var work=Context())
            await Assert.ThrowsAsync<DbUpdateException>(()=>Service(work).ReviewChangeRequestAsync(command.CommandId,Guid.NewGuid(),new(true),default));
        await using var verify=Context();
        Assert.Equal(original,await ApprovalSchedule(verify));Assert.Empty(await verify.Set<SubscriptionPause>().ToArrayAsync());
        Assert.Equal("PENDING_APPROVAL",(await verify.Set<PackageChangeRequest>().SingleAsync()).Status);
        Assert.Equal(command.ExpectedRowVersion,(await verify.Orders.SingleAsync()).RowVersion);
    }

    [UpcomingPostgresFact]
    public async Task Package_approval_HTTP_customer_cannot_accept_decline_or_list_staff_queue()
    {
        var command=await ApprovalPauseCommand();
        await using var factory=new ApiFactory(connection,services=>{
            services.AddSingleton<TimeProvider>(clock);
            services.PostConfigure<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>("Bearer", o => {
                o.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("test-only-key-at-least-thirty-two-characters-long"));
                o.TokenValidationParameters.ValidIssuer="DietTime.Tests";o.TokenValidationParameters.ValidAudience="DietTime.Tests";
            });
            services.PostConfigure<DietTime.Infrastructure.TwilioWhatsAppOptions>(o=>o.Enabled=false);
        });
        using var client=factory.CreateClient();
        var path=$"/api/v1/orders/{order.Id}";
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsJsonAsync(path+"/pause",command)).StatusCode);
        var token=new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("DietTime.Tests","DietTime.Tests",[new Claim(ClaimTypes.NameIdentifier,userId.ToString()),new Claim(ClaimTypes.Role,"Customer")],expires:DateTime.UtcNow.AddMinutes(5),signingCredentials:new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("test-only-key-at-least-thirty-two-characters-long")),SecurityAlgorithms.HmacSha256)));
        client.DefaultRequestHeaders.Authorization=new("Bearer",token);
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync(path+"/pause",command)).StatusCode);
        Assert.Null((await client.GetFromJsonAsync<PackageManagementResponse>(path+"/package-management"))!.CurrentPause);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/api/admin/orders/package-requests")).StatusCode);
        foreach(var approve in new[]{true,false})
            Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync($"/api/admin/orders/package-requests/{command.CommandId}/review",new PackageChangeReviewRequest(approve,"Review"))).StatusCode);
        Assert.Empty(await db.Set<SubscriptionPause>().ToArrayAsync());
        client.DefaultRequestHeaders.Authorization=null;
        client.DefaultRequestHeaders.Add("X-Development-User-Id",Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/api/admin/orders/package-requests")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync($"/api/admin/orders/package-requests/{command.CommandId}/review",new PackageChangeReviewRequest(true))).StatusCode);
        Assert.Single(await db.Set<SubscriptionPause>().AsNoTracking().ToArrayAsync());
    }
}
