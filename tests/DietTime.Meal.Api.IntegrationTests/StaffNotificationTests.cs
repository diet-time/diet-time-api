using System.Reflection;
using System.Net;
using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using DietTime.Application;
using DietTime.Domain;
using DietTime.Persistence;
using DietTime.Meal.Api.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;

namespace DietTime.Meal.Api.IntegrationTests;
public sealed class StaffNotificationCaptureTests
{
    private static void Capture(DietTimeDbContext db) => typeof(DietTimeDbContext).Assembly.GetType("DietTime.Persistence.StaffNotificationCapture")!
        .GetMethod("Capture",BindingFlags.Static|BindingFlags.Public)!.Invoke(null,[db]);
    private static DietTimeDbContext Context() => new(new DbContextOptionsBuilder<DietTimeDbContext>().UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").UseSnakeCaseNamingConvention().Options);
    [Fact]
    public void Save_capture_adds_order_and_fulfillment_events_once_without_touching_source() {
        using var db=Context();
        var order=Guid.NewGuid();var history=new OrderStatusHistory { Id=Guid.NewGuid(),OrderId=order,Status="PENDING_APPROVAL",ChangedAt=DateTimeOffset.UtcNow };
        db.Add(history);
        var day=new SubscriptionServiceDay { Id=Guid.NewGuid(),OrderId=order,Status="PLANNED",ServiceDate=new(2026,11,2) };db.Attach(day);
        day.Status="DELIVERED";day.FulfilledAt=DateTimeOffset.UtcNow;
        Capture(db);Capture(db);
        var events=db.ChangeTracker.Entries<StaffNotificationEvent>().Select(e=>e.Entity).ToArray();
        Assert.Equal(2,events.Length);Assert.Single(events.Where(e=>e.EventType=="ORDER_RECEIVED"));Assert.Single(events.Where(e=>e.EventType=="DELIVERED"));
        Assert.Equal("PENDING_APPROVAL",history.Status);Assert.Equal("DELIVERED",day.Status);
        Assert.Null(day.AutoFinalizedAt);Assert.False(day.SelectionLocked);
    }
    [Fact]
    public void Payment_only_history_and_unrelated_updates_do_not_emit_notifications() {
        using var db=Context();
        db.Add(new OrderStatusHistory { Id=Guid.NewGuid(),OrderId=Guid.NewGuid(),PreviousStatus="CONFIRMED",Status="CONFIRMED",PreviousPaymentStatus="PENDING",PaymentStatus="PAID" });
        var day=new SubscriptionServiceDay { Id=Guid.NewGuid(),OrderId=Guid.NewGuid() };db.Attach(day);day.ReviewReason="A meal selection needs review";
        Capture(db);Assert.Empty(db.ChangeTracker.Entries<StaffNotificationEvent>());
    }
    [Theory]
    [InlineData("PAUSE")]
    [InlineData("RESUME")]
    public void Package_requests_are_captured_once_and_unrelated_updates_are_ignored(string type) {
        using var db = Context();
        var request = new PackageChangeRequest { Id = Guid.NewGuid(), OrderId = Guid.NewGuid(),
            Type = type, CreatedAt = DateTimeOffset.UtcNow };
        db.Add(request);
        Capture(db); Capture(db);
        var notification = Assert.Single(db.ChangeTracker.Entries<StaffNotificationEvent>()).Entity;
        Assert.Equal($"PACKAGE_{type}_REQUESTED", notification.EventType);
        Assert.Equal(request.OrderId, notification.OrderId);
        Assert.Equal(request.CreatedAt, notification.CreatedAt);
        db.ChangeTracker.Clear();
        db.Attach(request); request.Reason = "Updated reason";
        Capture(db);
        Assert.Empty(db.ChangeTracker.Entries<StaffNotificationEvent>());
    }
    [Theory]
    [InlineData("PAUSE", "APPROVED")]
    [InlineData("PAUSE", "REJECTED")]
    [InlineData("RESUME", "APPROVED")]
    [InlineData("RESUME", "REJECTED")]
    public void Package_decisions_are_captured_once(string type, string status) {
        using var db = Context();
        var request = new PackageChangeRequest { Id = Guid.NewGuid(), OrderId = Guid.NewGuid(), Type = type };
        db.Attach(request);
        request.Status = status; request.ReviewedAt = DateTimeOffset.UtcNow;
        Capture(db); Capture(db);
        var notification = Assert.Single(db.ChangeTracker.Entries<StaffNotificationEvent>()).Entity;
        Assert.Equal($"PACKAGE_{type}_{status}", notification.EventType);
        Assert.Equal(request.ReviewedAt, notification.CreatedAt);
    }
    [Theory]
    [InlineData("Admin",true,true)]
    [InlineData("Meal Manager",false,false)]
    [InlineData("Customer",false,false)]
    [InlineData("Operations",false,false)]
    public async Task Inbox_and_order_write_policies_are_enforced(string role,bool inbox,bool write) {
        var services=new ServiceCollection();services.AddLogging();services.AddAuthorization();using var provider=services.BuildServiceProvider();
        var principal=new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role,role)],"Test"));
        var policies=provider.GetRequiredService<IAuthorizationPolicyProvider>();var authorization=provider.GetRequiredService<IAuthorizationService>();
        var inboxPolicy=await AuthorizationPolicy.CombineAsync(policies,typeof(StaffNotificationsController).GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal(inbox,(await authorization.AuthorizeAsync(principal,null,inboxPolicy!)).Succeeded);
        var attributes=typeof(StaffOrdersController).GetCustomAttributes<AuthorizeAttribute>().Concat(typeof(StaffOrdersController).GetMethod("Approve")!.GetCustomAttributes<AuthorizeAttribute>());
        var writePolicy=await AuthorizationPolicy.CombineAsync(policies,attributes);
        Assert.Equal(write,(await authorization.AuthorizeAsync(principal,null,writePolicy!)).Succeeded);
    }
    [Fact]
    public async Task Inbox_endpoints_reject_anonymous_and_customer_requests() {
        const string key="test-only-key-at-least-thirty-two-characters-long";
        await using var factory=new ApiFactory("Host=127.0.0.1;Port=54329;Database=unused;Username=unused;Password=unused;Timeout=1",services=>{
            services.RemoveAll<IHostedService>();
            services.PostConfigure<AuthenticationOptions>(o=>{o.DefaultScheme="Bearer";o.DefaultChallengeScheme="Bearer";});
            services.PostConfigure<JwtBearerOptions>("Bearer",o=>{o.TokenValidationParameters.IssuerSigningKey=new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));o.TokenValidationParameters.ValidIssuer="DietTime.Tests";o.TokenValidationParameters.ValidAudience="DietTime.Tests";});
        });
        using var client=factory.CreateClient();
        var paths=new[] {"/api/admin/notifications","/api/admin/notifications/unread-count","/api/admin/notifications/read-all",$"/api/admin/notifications/{Guid.NewGuid()}/read"};
        foreach(var path in paths) Assert.Equal(HttpStatusCode.Unauthorized,(await client.SendAsync(new HttpRequestMessage(path.EndsWith("read-all")||path.EndsWith("/read")?HttpMethod.Patch:HttpMethod.Get,path))).StatusCode);
        var token=new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("DietTime.Tests","DietTime.Tests",[new Claim(ClaimTypes.NameIdentifier,Guid.NewGuid().ToString()),new Claim(ClaimTypes.Role,"Customer")],expires:DateTime.UtcNow.AddMinutes(5),signingCredentials:new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),SecurityAlgorithms.HmacSha256)));
        client.DefaultRequestHeaders.Authorization=new("Bearer",token);
        foreach(var path in paths) Assert.Equal(HttpStatusCode.Forbidden,(await client.SendAsync(new HttpRequestMessage(path.EndsWith("read-all")||path.EndsWith("/read")?HttpMethod.Patch:HttpMethod.Get,path))).StatusCode);
    }
}

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Staff_inbox_persists_role_scoped_events_and_read_receipts_without_retry_duplicates() {
        var adminRole=new IdentityRole<Guid>("Admin"){Id=Guid.NewGuid()};
        var manager=Guid.NewGuid();db.Users.Add(new ApplicationUser {Id=manager,UserName="manager",NormalizedUserName="MANAGER"});db.Roles.Add(adminRole);
        db.UserRoles.AddRange(new IdentityUserRole<Guid>{UserId=userId,RoleId=adminRole.Id},new IdentityUserRole<Guid>{UserId=manager,RoleId=adminRole.Id});await db.SaveChangesAsync();
        var history=new OrderStatusHistory {Id=Guid.NewGuid(),OrderId=order.Id,Status="PENDING_APPROVAL",ChangedAt=clock.Now};db.Add(history);await db.SaveChangesAsync();
        var service=new StaffNotificationService(db,clock);await service.ProjectAsync(default);await service.ProjectAsync(default);
        Assert.Equal(1,await service.UnreadAsync(userId,true,default));Assert.Equal(1,await service.UnreadAsync(manager,true,default));
        var receipt=(await service.ListAsync(userId,true,1,20,default)).Items.Single();
        Assert.False(await service.ReadAsync(manager,true,receipt.Id,default));
        Assert.True(await service.ReadAsync(userId,true,receipt.Id,default));
        var readAt=(await service.ListAsync(userId,true,1,20,default)).Items.Single().ReadAt;
        clock.Now=clock.Now.AddMinutes(1);Assert.True(await service.ReadAsync(userId,true,receipt.Id,default));Assert.Equal(readAt,(await service.ListAsync(userId,true,1,20,default)).Items.Single().ReadAt);
        await service.ReadAllAsync(manager,true,default);
        db.Add(new OrderStatusHistory {Id=Guid.NewGuid(),OrderId=order.Id,PreviousStatus="PENDING_APPROVAL",Status="CONFIRMED",ChangedAt=clock.Now});await db.SaveChangesAsync();await service.ProjectAsync(default);
        Assert.Equal(1,await service.UnreadAsync(userId,true,default));Assert.Equal(1,await service.UnreadAsync(manager,true,default));
        await service.ReadAllAsync(manager,true,default);Assert.Equal(0,await service.UnreadAsync(manager,true,default));Assert.Equal(1,await service.UnreadAsync(userId,true,default));
        await using var fresh=Context();var persisted=new StaffNotificationService(fresh,clock);Assert.Equal(0,await persisted.UnreadAsync(manager,true,default));
        Assert.Equal("ORDER_APPROVED",(await persisted.ListAsync(userId,true,1,20,default)).Items[0].EventType);
    }

    [UpcomingPostgresFact]
    public async Task Staff_inbox_projects_overdue_and_fulfillment_once_using_existing_Qatar_deadlines() {
        var role=new IdentityRole<Guid>("Admin"){Id=Guid.NewGuid()};db.Roles.Add(role);db.UserRoles.Add(new IdentityUserRole<Guid>{UserId=userId,RoleId=role.Id});await db.SaveChangesAsync();
        var menu=await PrepareAsync();order.DeliveryTimeSlotName="Morning";order.DeliveryStartTime=new(7,0);order.DeliveryEndTime=new(11,0);await db.SaveChangesAsync();
        var service=new StaffNotificationService(db,clock);clock.Now=new(2026,10,27,7,30,0,TimeSpan.Zero);await service.ProjectAsync(default);Assert.Equal(0,await service.UnreadAsync(userId,true,default));
        clock.Now=new(2026,10,27,8,1,0,TimeSpan.Zero);await service.ProjectAsync(default);await service.ProjectAsync(default);Assert.Equal(1,await service.UnreadAsync(userId,true,default));
        Assert.Equal("DELIVERY_OVERDUE",(await service.ListAsync(userId,true,1,20,default)).Items.Single().EventType);
        await new ServiceDayFulfillmentService(db,clock).UpdateAsync(menu.SubscriptionServiceDayId!.Value,"DELIVERED",userId,default);
        await service.ProjectAsync(default);await service.ProjectAsync(default);
        Assert.Equal(2,await service.UnreadAsync(userId,true,default));Assert.Single(await db.StaffNotifications.Where(n=>n.EventType=="DELIVERED").ToArrayAsync());
    }
}
