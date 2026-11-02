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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Authorization;
using DietTime.Meal.Api.Controllers;

namespace DietTime.Meal.Api.IntegrationTests;
public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Fulfillment_endpoint_is_idempotent_scoped_and_updates_summary_without_changing_preparation()
    {
        var setup = await ApprovalClient(); await using var factory = setup.Factory; using var client = setup.Client;
        var orderId = (await SubmitApprovalOrder(client, setup.Request)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Review(client, orderId, "approve", 1)).StatusCode);
        var days = await db.Set<SubscriptionServiceDay>().AsNoTracking().Where(d => d.OrderId == orderId).OrderBy(d => d.ServiceDate).ToArrayAsync();
        var day = days[0];
        clock.Now = new DateTimeOffset(day.ServiceDate.AddDays(-1).ToDateTime(new TimeOnly(9, 0)), TimeSpan.FromHours(3)).ToUniversalTime();
        var calendar = new AdminDeliveryCalendarService(db, Options.Create(new MenuRotationOptions()), clock);
        var before = await calendar.GetPreparationSummaryAsync(day.ServiceDate, default);
        var route = $"/api/admin/subscription-service-days/{day.Id}/fulfillment";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsJsonAsync(route, new { status = "DELIVERED" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PatchAsJsonAsync(route, new { status = "COLLECTED" })).StatusCode);
        Assert.Null((await db.Set<SubscriptionServiceDay>().AsNoTracking().SingleAsync(d => d.Id == day.Id)).FulfilledAt);
        clock.Now = new DateTimeOffset(day.ServiceDate.ToDateTime(new TimeOnly(9, 0)), TimeSpan.FromHours(3)).ToUniversalTime();
        var results = await Task.WhenAll(client.PatchAsJsonAsync(route, new { status = "COLLECTED" }), client.PatchAsJsonAsync(route, new { status = "COLLECTED" }));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var first = (await results[0].Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var second = (await results[1].Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(first.GetProperty("fulfilledAt").GetString(), second.GetProperty("fulfilledAt").GetString());
        Assert.Equal(userId, first.GetProperty("fulfilledBy").GetGuid());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PatchAsJsonAsync(route, new { status = "DELIVERED" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync($"/api/admin/subscription-service-days/{Guid.NewGuid()}/fulfillment", new { status = "DELIVERED" })).StatusCode);
        var saved = await db.Set<SubscriptionServiceDay>().AsNoTracking().Where(d => d.OrderId == orderId).ToArrayAsync();
        Assert.Single(saved.Where(d => d.Status == "COLLECTED"));
        foreach (var other in saved.Where(d => d.Id != day.Id)) {
            Assert.Equal(days.Single(d => d.Id == other.Id).Status, other.Status);
            Assert.Null(other.FulfilledAt);
        }
        var savedOrder = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal("CONFIRMED", savedOrder.Status); Assert.Equal("PENDING", savedOrder.PaymentStatus);
        var after = await calendar.GetPreparationSummaryAsync(day.ServiceDate, default);
        Assert.Equal(before.CollectedCount + 1, after.CollectedCount);
        Assert.Equal(before.RemainingCount - 1, after.RemainingCount);
        Assert.Equal(before.TotalScheduledCount, after.TotalScheduledCount);
        Assert.Equal(before.MealItemCount, after.MealItemCount);
        Assert.Equal(before.MealTypes, after.MealTypes);
        Assert.False(after.Selections.Single(s => s.ServiceDayId == day.Id).FinalizationStatus == "AUTO_FINALIZED");
        var pdf = await client.GetAsync($"/api/admin/delivery-calendar/{day.ServiceDate:yyyy-MM-dd}/preparation-report");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode); Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        var bytes = await pdf.Content.ReadAsByteArrayAsync(); Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
        // Complete a home-delivery day through the same endpoint.
        await Service().InitializeAsync(order, default);
        var homeDay = await db.Set<SubscriptionServiceDay>().AsNoTracking().FirstAsync(d => d.OrderId == order.Id);
        clock.Now = new DateTimeOffset(homeDay.ServiceDate.ToDateTime(new TimeOnly(9, 0)), TimeSpan.FromHours(3)).ToUniversalTime();
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"/api/admin/subscription-service-days/{homeDay.Id}/fulfillment", new { status = "DELIVERED" })).StatusCode);
        Assert.Equal("DELIVERED", (await db.Set<SubscriptionServiceDay>().AsNoTracking().SingleAsync(d => d.Id == homeDay.Id)).Status);
    }

}

public sealed class FulfillmentAuthorizationTests
{
    [Theory]
    [InlineData("Admin", true)]
    [InlineData("Meal Manager", false)]
    [InlineData("Customer", false)]
    [InlineData("Operations", false)]
    [InlineData("Viewer", false)]
    public async Task Fulfillment_action_requires_the_expected_staff_role(string role, bool expected)
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddAuthorization();
        using var provider = services.BuildServiceProvider();
        var attributes = typeof(ServiceDayFulfillmentController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<IAuthorizeData>();
        var policy = await AuthorizationPolicy.CombineAsync(provider.GetRequiredService<IAuthorizationPolicyProvider>(), attributes);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "Test"));
        var result = await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(principal, null, policy!);
        Assert.Equal(expected, result.Succeeded);
    }
    [Fact]
    public async Task Fulfillment_endpoint_rejects_customer_and_anonymous_requests_before_business_storage()
    {
        const string key = "test-only-key-at-least-thirty-two-characters-long";
        // No business database is needed: authorization must reject these requests before the action runs.
        await using var factory = new ApiFactory("Host=127.0.0.1;Port=54329;Database=unused;Username=unused;Password=unused;Timeout=1", services => {
            services.RemoveAll<IHostedService>();
            services.PostConfigure<AuthenticationOptions>(o => { o.DefaultScheme = "Bearer"; o.DefaultChallengeScheme = "Bearer"; });
            services.PostConfigure<JwtBearerOptions>("Bearer", o => {
                o.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
                o.TokenValidationParameters.ValidIssuer = "DietTime.Tests";
                o.TokenValidationParameters.ValidAudience = "DietTime.Tests";
            });
        });
        using var client = factory.CreateClient();
        var route = $"/api/admin/subscription-service-days/{Guid.NewGuid()}/fulfillment";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PatchAsJsonAsync(route, new { status = "DELIVERED" })).StatusCode);
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("DietTime.Tests", "DietTime.Tests",
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, "Customer")],
            expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256)));
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PatchAsJsonAsync(route, new { status = "DELIVERED" })).StatusCode);
    }
}
