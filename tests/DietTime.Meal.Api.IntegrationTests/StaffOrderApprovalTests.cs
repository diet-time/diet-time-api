using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DietTime.Meal.Api.IntegrationTests;
public sealed partial class UpcomingMealServiceTests
{
    private async Task<(ApiFactory Factory,HttpClient Client,PlaceOrderRequest Request)> ApprovalClient(bool prepaid=false)
    {
        clock.Now=new(2026,10,26,10,0,0,TimeSpan.Zero);
        var branch=new TakeawayBranch { Id=Guid.NewGuid(),Name="Diet Time – Azizia Branch",IsActive=true };
        db.Add(branch);await db.SaveChangesAsync();
        var factory=new ApiFactory(connection,services=>{
            services.AddSingleton<TimeProvider>(clock);
            services.PostConfigure<OrderOptions>(o=>o.RequiresPrepayment=prepaid);
            services.PostConfigure<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>("Bearer", o => {
                o.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("test-only-key-at-least-thirty-two-characters-long"));
                o.TokenValidationParameters.ValidIssuer="DietTime.Tests";o.TokenValidationParameters.ValidAudience="DietTime.Tests";
            });
            services.PostConfigure<DietTime.Infrastructure.TwilioWhatsAppOptions>(o=>o.Enabled=false);
        });
        var client=factory.CreateClient();client.DefaultRequestHeaders.Add("X-Development-User-Id",userId.ToString());
        var price=await db.MealPlanPrices.SingleAsync();
        return(factory,client,new(order.CustomerProfileId,order.MealPlanTemplateId,price.Id,null,null,new(2026,10,27),[1,2,3,4,6,7],FulfillmentMethod:"TAKEAWAY",TakeawayBranchId:branch.Id));
    }
    private static async Task<JsonElement> SubmitApprovalOrder(HttpClient client,PlaceOrderRequest body,string? key=null)
    {
        using var message=new HttpRequestMessage(HttpMethod.Post,"/api/v1/orders"){Content=JsonContent.Create(body)};
        message.Headers.Add("Idempotency-Key",key??Guid.NewGuid().ToString());
        var response=await client.SendAsync(message);Assert.Equal(HttpStatusCode.Created,response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    private static Task<HttpResponseMessage> Review(HttpClient client,Guid id,string action,long version,string? reason=null) =>
        client.PostAsJsonAsync($"/api/admin/orders/{id}/{action}",new OrderReviewRequest(version,reason));

    [UpcomingPostgresFact]
    public async Task Submission_is_pending_idempotent_unfulfilled_and_staff_review_is_audited()
    {
        var setup=await ApprovalClient();await using var factory=setup.Factory;using var client=setup.Client;
        var key=Guid.NewGuid().ToString();var submitted=await SubmitApprovalOrder(client,setup.Request,key);var id=submitted.GetProperty("id").GetGuid();
        Assert.Equal("PENDING_APPROVAL",submitted.GetProperty("status").GetString());
        Assert.Equal("PENDING",submitted.GetProperty("paymentStatus").GetString());
        Assert.False(await db.Set<SubscriptionServiceDay>().AnyAsync(d=>d.OrderId==id));
        Assert.Equal(2,await db.OrderNotifications.CountAsync(n=>n.OrderId==id));
        Assert.Equal(id,(await SubmitApprovalOrder(client,setup.Request,key)).GetProperty("id").GetGuid());
        Assert.Equal(2,await db.OrderNotifications.CountAsync(n=>n.OrderId==id));
        var preparation=await new AdminDeliveryCalendarService(db,Options.Create(new MenuRotationOptions()),clock).GetPreparationSummaryAsync(new(2026,10,27),default);
        Assert.Equal(1,preparation.OrderCount); // Only the historical approved seed order.
        var tomorrow=await client.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{id}/tomorrow-menu");Assert.False(tomorrow.GetProperty("data").GetProperty("canConfirm").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest,(await Review(client,id,"hold",1,"")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await Review(client,id,"hold",1,"Please confirm the pickup time")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await Review(client,id,"approve",2)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await Review(client,id,"resume",2)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await Review(client,id,"approve",3)).StatusCode);
        var saved=await db.Orders.AsNoTracking().SingleAsync(o=>o.Id==id);
        Assert.Equal("CONFIRMED",saved.Status);Assert.Equal("PENDING",saved.PaymentStatus);Assert.Equal(userId,saved.ApprovedBy);Assert.NotNull(saved.ApprovedAt);
        Assert.Equal(12,await db.Set<SubscriptionServiceDay>().CountAsync(d=>d.OrderId==id));
        Assert.Equal(HttpStatusCode.Conflict,(await Review(client,id,"approve",3)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await Review(client,id,"approve",4)).StatusCode);
        Assert.Single(await db.OrderStatusHistory.Where(h=>h.OrderId==id&&h.PreviousStatus=="PENDING_APPROVAL"&&h.Status=="CONFIRMED").ToArrayAsync());
        Assert.Single(await db.OrderNotifications.Where(n=>n.OrderId==id&&n.EventCode=="ORDER_APPROVED").ToArrayAsync());
        Assert.Equal("CONFIRMED",(await db.Orders.AsNoTracking().SingleAsync(o=>o.Id==order.Id)).Status);
    }

    [UpcomingPostgresFact]
    public async Task Competing_approval_and_rejection_have_exactly_one_winner()
    {
        var setup=await ApprovalClient();await using var factory=setup.Factory;using var client=setup.Client;
        var id=(await SubmitApprovalOrder(client,setup.Request)).GetProperty("id").GetGuid();
        var results=await Task.WhenAll(Review(client,id,"approve",1),Review(client,id,"reject",1,"Cannot supply this package"));
        Assert.Single(results.Where(r=>r.StatusCode==HttpStatusCode.OK));Assert.Single(results.Where(r=>r.StatusCode==HttpStatusCode.Conflict));
        Assert.Equal(2,await db.OrderStatusHistory.CountAsync(h=>h.OrderId==id));
        Assert.Equal(3,await db.OrderNotifications.CountAsync(n=>n.OrderId==id));
        var saved=await db.Orders.AsNoTracking().SingleAsync(o=>o.Id==id);
        Assert.Equal(saved.Status=="CONFIRMED"?12:0,await db.Set<SubscriptionServiceDay>().CountAsync(d=>d.OrderId==id));
        var second=(await SubmitApprovalOrder(client,setup.Request)).GetProperty("id").GetGuid();
        var duplicate=await Task.WhenAll(Review(client,second,"approve",1),Review(client,second,"approve",1));
        Assert.Single(duplicate.Where(r=>r.StatusCode==HttpStatusCode.OK));Assert.Single(duplicate.Where(r=>r.StatusCode==HttpStatusCode.Conflict));
        Assert.Equal(12,await db.Set<SubscriptionServiceDay>().CountAsync(d=>d.OrderId==second));
        Assert.Single(await db.OrderNotifications.Where(n=>n.OrderId==second&&n.EventCode=="ORDER_APPROVED").ToArrayAsync());
    }

    [UpcomingPostgresFact]
    public async Task Prepayment_remains_independent_and_failed_payment_blocks_fulfillment()
    {
        var setup=await ApprovalClient(true);await using var factory=setup.Factory;using var client=setup.Client;
        var id=(await SubmitApprovalOrder(client,setup.Request)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK,(await Review(client,id,"approve",1)).StatusCode);
        Assert.False(await db.Set<SubscriptionServiceDay>().AnyAsync(d=>d.OrderId==id));
        var detail=await client.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{id}");Assert.True(detail.GetProperty("review").GetProperty("awaitingPayment").GetBoolean());
        var paid=await client.PostAsJsonAsync($"/api/admin/orders/{id}/payment",new RecordOrderPaymentRequest(2,"PAID","Cash receipt 123"));Assert.Equal(HttpStatusCode.OK,paid.StatusCode);
        Assert.Equal(12,await db.Set<SubscriptionServiceDay>().CountAsync(d=>d.OrderId==id));
        Assert.Equal(HttpStatusCode.Conflict,(await Review(client,id,"activate",3)).StatusCode);
        clock.Now=new(2026,10,27,10,0,0,TimeSpan.Zero);
        Assert.Equal(HttpStatusCode.OK,(await Review(client,id,"activate",3)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync($"/api/admin/orders/{id}/payment",new RecordOrderPaymentRequest(4,"FAILED","Payment reversed by staff"))).StatusCode);
        var saved=await db.Orders.AsNoTracking().SingleAsync(o=>o.Id==id);Assert.Equal("ACTIVE",saved.Status);Assert.False(OrderWorkflowPolicy.CanFulfill(saved));
        var summary=await new AdminDeliveryCalendarService(db,Options.Create(new MenuRotationOptions()),clock).GetPreparationSummaryAsync(new(2026,10,27),default);Assert.DoesNotContain(summary.Selections,s=>s.OrderReference==saved.OrderNumber);
        Assert.Equal(HttpStatusCode.Conflict,(await Review(client,id,"complete",5)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync($"/api/admin/orders/{id}/payment",new RecordOrderPaymentRequest(5,"PAID","Replacement payment verified"))).StatusCode);
        await db.Set<SubscriptionServiceDay>().Where(d=>d.OrderId==id).ExecuteUpdateAsync(s=>s.SetProperty(d=>d.Status,"DELIVERED"));
        Assert.Equal(HttpStatusCode.OK,(await Review(client,id,"complete",6)).StatusCode);
    }

    [UpcomingPostgresFact]
    public async Task Late_prepayment_requires_explicit_date_revision_before_service_initialization()
    {
        var setup=await ApprovalClient(true);await using var factory=setup.Factory;using var client=setup.Client;
        var id=(await SubmitApprovalOrder(client,setup.Request)).GetProperty("id").GetGuid();await Review(client,id,"approve",1);
        clock.Now=new(2026,10,29,10,0,0,TimeSpan.Zero);
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync($"/api/admin/orders/{id}/payment",new RecordOrderPaymentRequest(2,"PAID","Late cash receipt"))).StatusCode);
        var saved=await db.Orders.AsNoTracking().SingleAsync(o=>o.Id==id);Assert.NotNull(saved.FulfillmentBlockedReason);Assert.False(OrderWorkflowPolicy.CanFulfill(saved));
        Assert.Equal(new DateOnly(2026,10,27),saved.StartDate);Assert.False(await db.Set<SubscriptionServiceDay>().AnyAsync(d=>d.OrderId==id));
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync($"/api/admin/orders/{id}/revise-dates",new ReviseOrderDatesRequest(3,new(2026,10,31),[1,2,3,4,6,7],"Customer agreed to start on Saturday"))).StatusCode);
        Assert.Equal(12,await db.Set<SubscriptionServiceDay>().CountAsync(d=>d.OrderId==id));
    }

    [UpcomingPostgresFact]
    public async Task Stale_dates_need_explicit_revision_and_inactive_branch_blocks_approval()
    {
        var setup=await ApprovalClient();await using var factory=setup.Factory;using var client=setup.Client;
        var id=(await SubmitApprovalOrder(client,setup.Request)).GetProperty("id").GetGuid();clock.Now=new(2026,10,28,10,0,0,TimeSpan.Zero);
        Assert.Equal(HttpStatusCode.Conflict,(await Review(client,id,"approve",1)).StatusCode);
        Assert.Equal(new DateOnly(2026,10,27),(await db.Orders.AsNoTracking().SingleAsync(o=>o.Id==id)).StartDate);
        var revise=await client.PostAsJsonAsync($"/api/admin/orders/{id}/revise-dates",new ReviseOrderDatesRequest(1,new(2026,10,29),[1,2,3,4,6,7],"Customer agreed revised dates"));Assert.Equal(HttpStatusCode.OK,revise.StatusCode);
        var branch=await db.TakeawayBranches.SingleAsync();branch.IsActive=false;await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Conflict,(await Review(client,id,"approve",2)).StatusCode);
        branch.IsActive=true;await db.SaveChangesAsync();
        var price=await db.MealPlanPrices.SingleAsync();price.IsActive=false;await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Conflict,(await Review(client,id,"approve",2)).StatusCode);
        price.IsActive=true;await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.OK,(await Review(client,id,"approve",2)).StatusCode);
        Assert.Equal(12,await db.Set<SubscriptionServiceDay>().CountAsync(d=>d.OrderId==id));
    }

    [UpcomingPostgresFact]
    public async Task Paid_rejection_flags_refund_and_customer_cancellation_respects_ownership_and_start_date()
    {
        var setup=await ApprovalClient();await using var factory=setup.Factory;using var client=setup.Client;
        var id=(await SubmitApprovalOrder(client,setup.Request)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync($"/api/admin/orders/{id}/payment",new RecordOrderPaymentRequest(1,"PAID","Offline payment verified"))).StatusCode);
        Assert.False(await db.Set<SubscriptionServiceDay>().AnyAsync(d=>d.OrderId==id));
        Assert.Equal(HttpStatusCode.OK,(await Review(client,id,"reject",2,"Unable to fulfill requested package")).StatusCode);
        var saved=await db.Orders.AsNoTracking().SingleAsync(o=>o.Id==id);Assert.Equal("REFUND_PENDING",saved.PaymentStatus);Assert.Equal("REJECTED",saved.Status);Assert.Equal(userId,saved.RejectedBy);
        var next=(await SubmitApprovalOrder(client,setup.Request)).GetProperty("id").GetGuid();
        client.DefaultRequestHeaders.Remove("X-Development-User-Id");client.DefaultRequestHeaders.Add("X-Development-User-Id",Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NotFound,(await client.PostAsJsonAsync($"/api/v1/orders/{next}/cancel",new OrderReviewRequest(1,"Changed my plans"))).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Development-User-Id");client.DefaultRequestHeaders.Add("X-Development-User-Id",userId.ToString());
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync($"/api/v1/orders/{next}/cancel",new OrderReviewRequest(1,"Changed my plans"))).StatusCode);
        var paidBeforeApproval=(await SubmitApprovalOrder(client,setup.Request)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync($"/api/admin/orders/{paidBeforeApproval}/payment",new RecordOrderPaymentRequest(1,"PAID","Cash payment before review"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await Review(client,paidBeforeApproval,"approve",2)).StatusCode);
        Assert.Equal("PAID",(await db.Orders.AsNoTracking().SingleAsync(o=>o.Id==paidBeforeApproval)).PaymentStatus);
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync($"/api/v1/orders/{paidBeforeApproval}/cancel",new OrderReviewRequest(3,"Cancelled before preparation"))).StatusCode);
        Assert.Equal("REFUND_PENDING",(await db.Orders.AsNoTracking().SingleAsync(o=>o.Id==paidBeforeApproval)).PaymentStatus);
        var confirmed=(await SubmitApprovalOrder(client,setup.Request)).GetProperty("id").GetGuid();await Review(client,confirmed,"approve",1);
        clock.Now=new(2026,10,27,10,0,0,TimeSpan.Zero);
        Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync($"/api/v1/orders/{confirmed}/cancel",new OrderReviewRequest(2,"Changed my plans"))).StatusCode);
    }

    [UpcomingPostgresFact]
    public async Task Customers_cannot_review_orders_and_missing_templates_are_retained_durably()
    {
        var setup=await ApprovalClient();await using var factory=setup.Factory;using var client=setup.Client;
        var id=(await SubmitApprovalOrder(client,setup.Request)).GetProperty("id").GetGuid();
        var token=new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("DietTime.Tests","DietTime.Tests",[new Claim(ClaimTypes.NameIdentifier,userId.ToString()),new Claim(ClaimTypes.Role,"Customer")],expires:DateTime.UtcNow.AddMinutes(5),signingCredentials:new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("test-only-key-at-least-thirty-two-characters-long")),SecurityAlgorithms.HmacSha256)));
        client.DefaultRequestHeaders.Authorization=new("Bearer",token);
        Assert.Equal(HttpStatusCode.Forbidden,(await Review(client,id,"approve",1)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/api/admin/orders")).StatusCode);
        client.DefaultRequestHeaders.Authorization=null;
        using var scope=factory.Services.CreateScope();await scope.ServiceProvider.GetRequiredService<IOrderNotificationSender>().SendAsync(id,default);
        var customer=await db.OrderNotifications.AsNoTracking().SingleAsync(n=>n.OrderId==id&&n.Audience=="CUSTOMER");
        // A second worker may have claimed the row; it must still exist and never be marked sent.
        Assert.Contains(customer.Status,new[]{"WAITING_CONFIGURATION","PROCESSING"});Assert.Null(customer.SentAt);
        Assert.Equal("PENDING_APPROVAL",(await db.Orders.AsNoTracking().SingleAsync(o=>o.Id==id)).Status);
    }
}
