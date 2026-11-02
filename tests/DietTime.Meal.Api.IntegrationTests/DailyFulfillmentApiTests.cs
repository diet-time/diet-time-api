using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using DietTime.Application;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DietTime.Meal.Api.IntegrationTests;
public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public Task Admin_delivers_service_day() => FulfillAsStaff("Admin", "HOME_DELIVERY", "DELIVERED");
    [UpcomingPostgresFact]
    public Task Admin_collects_service_day() => FulfillAsStaff("Admin", "TAKEAWAY", "COLLECTED");

    private async Task FulfillAsStaff(string role, string mode, string status)
    {
        var setup = await ApprovalClient(); await using var factory = setup.Factory; using var client = setup.Client;
        order.FulfillmentMethod = mode; await db.SaveChangesAsync();
        var menu = await PrepareAsync();
        var dayId = menu.SubscriptionServiceDayId!.Value;
        var before = await db.Set<SubscriptionServiceDay>().AsNoTracking().Where(d => d.OrderId == order.Id).ToDictionaryAsync(d => d.Id);
        var selectionCount = await db.Set<CustomerMealSelection>().CountAsync();
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("DietTime.Tests", "DietTime.Tests",
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, role)],
            expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("test-only-key-at-least-thirty-two-characters-long")), SecurityAlgorithms.HmacSha256)));
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var route = $"/api/admin/subscription-service-days/{dayId}/fulfillment";
        clock.Now = new DateTimeOffset(before[dayId].ServiceDate.ToDateTime(new TimeOnly(9, 0)), TimeSpan.FromHours(3)).ToUniversalTime();
        var results = await Task.WhenAll(client.PatchAsJsonAsync(route,new { status }), client.PatchAsJsonAsync(route,new { status }));
        Assert.All(results,r => Assert.Equal(HttpStatusCode.OK,r.StatusCode));
        var after = await db.Set<SubscriptionServiceDay>().AsNoTracking().Where(d => d.OrderId == order.Id).ToArrayAsync();
        var fulfilled = after.Single(d => d.Id == dayId);
        Assert.Equal(status,fulfilled.Status); Assert.Equal(userId,fulfilled.FulfilledBy); Assert.Equal(clock.Now,fulfilled.FulfilledAt);
        Assert.Equal(before[dayId].Revision + 1,fulfilled.Revision);
        foreach (var other in after.Where(d => d.Id != dayId)) { Assert.Equal(before[other.Id].Status,other.Status); Assert.Equal(before[other.Id].Revision,other.Revision); }
        Assert.Equal(selectionCount,await db.Set<CustomerMealSelection>().CountAsync());
        var savedOrder = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal("CONFIRMED",savedOrder.Status); Assert.Equal(order.PaymentStatus,savedOrder.PaymentStatus);
    }

    [UpcomingPostgresFact]
    public async Task Calendar_aggregates_persisted_days_with_correct_overdue_partial_and_fulfilled_priority()
    {
        var setup = await ApprovalClient(); await using var factory = setup.Factory; using var client = setup.Client;
        var menu = await PrepareAsync();
        order.DeliveryTimeSlotName = "Morning"; order.DeliveryStartTime = new(7,0); order.DeliveryEndTime = new(11,0); await db.SaveChangesAsync();
        var takeawayId = (await SubmitApprovalOrder(client,setup.Request)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK,(await Review(client,takeawayId,"approve",1)).StatusCode);
        clock.Now = new(2026,10,27,8,1,0,TimeSpan.Zero);
        var calendar = new AdminDeliveryCalendarService(db,Options.Create(new MenuRotationOptions()),clock);
        var date = new DateOnly(2026,10,27);
        var month = await calendar.GetMonthAsync(new(2026,10,1),new(2026,10,31),null,null,default);
        var day = month.Days.Single(d => d.Date == date);
        Assert.Equal(2,day.TotalServiceDays); Assert.Equal(1,day.OverdueCount); Assert.Equal(1,day.PendingCount); Assert.Equal("OVERDUE",day.FulfillmentProgress);
        Assert.Equal(2,day.OrderCount); Assert.Equal(1,day.CustomerCount);
        Assert.All(month.Days.Where(d => d.Date > date),d => Assert.Equal(0,d.OverdueCount));
        Assert.All(month.Days.Where(d => d.Date.DayOfWeek == DayOfWeek.Friday),d => { Assert.Equal(0,d.TotalServiceDays); Assert.Equal("NO_DELIVERIES",d.FulfillmentProgress); });
        Assert.Equal(HttpStatusCode.OK,(await client.PatchAsJsonAsync($"/api/admin/subscription-service-days/{menu.SubscriptionServiceDayId}/fulfillment",new { status = "DELIVERED" })).StatusCode);
        var partial = (await calendar.GetMonthAsync(date,date,null,null,default)).Days.Single();
        Assert.Equal("PARTIALLY_FULFILLED",partial.FulfillmentProgress); Assert.Equal(1,partial.PendingCount); Assert.Equal(1,partial.FulfilledCount);
        var takeawayDay = await db.Set<SubscriptionServiceDay>().AsNoTracking().SingleAsync(d => d.OrderId == takeawayId && d.ServiceDate == date);
        Assert.Equal(HttpStatusCode.OK,(await client.PatchAsJsonAsync($"/api/admin/subscription-service-days/{takeawayDay.Id}/fulfillment",new { status = "COLLECTED" })).StatusCode);
        var fulfilled = (await calendar.GetMonthAsync(date,date,null,null,default)).Days.Single();
        Assert.Equal("FULFILLED",fulfilled.FulfillmentProgress); Assert.Equal(2,fulfilled.FulfilledCount); Assert.Equal(0,fulfilled.PendingCount); Assert.Equal(0,fulfilled.OverdueCount);
        Assert.Equal(fulfilled.TotalServiceDays,fulfilled.PendingCount+fulfilled.OverdueCount+fulfilled.FulfilledCount);
    }

}
