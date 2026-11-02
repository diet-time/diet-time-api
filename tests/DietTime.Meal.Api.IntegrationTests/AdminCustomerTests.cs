using System.Net;
using System.Net.Http.Json;
using DietTime.Contracts;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Identity;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Customer_directory_counts_orders_once_and_searches_contacts_including_zero_booking_profiles()
    {
        await PrepareAsync();
        clock.Now = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);
        var profile = await db.CustomerProfiles.SingleAsync(p => p.Id == order.CustomerProfileId);
        profile.PreferredName = "Legacy name";
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        user.Email = "sara@example.test"; user.PhoneNumber = "+97455555555";
        var person = await db.UserProfiles.SingleOrDefaultAsync(p => p.UserId == userId);
        if (person == null) { person = new UserProfile { Id = Guid.NewGuid(), UserId = userId }; db.Add(person); }
        person.FirstName = "Sara"; person.LastName = "Ali";
        var today = new DateOnly(2026, 10, 28);
        foreach (var status in new[] { "ACTIVE", "COMPLETED", "PENDING_APPROVAL", "CANCELLED", "REJECTED", "CONFIRMED" })
        {
            db.Orders.Add(new Order
            {
                Id = Guid.NewGuid(), CustomerProfileId = profile.Id, MealPlanTemplateId = order.MealPlanTemplateId,
                MealPlanPriceId = order.MealPlanPriceId, OrderNumber = "ORD-" + status, IdempotencyKey = Guid.NewGuid().ToString(),
                Status = status, PaymentStatus = "PENDING", RequiresPrepayment = status == "CONFIRMED",
                PlanName = "Balance", PlanDurationName = "12 days", StartDate = today.AddDays(-1), EndDate = today.AddDays(12),
                PlacedAt = clock.Now, CreatedAt = clock.Now, UpdatedAt = clock.Now
            });
        }
        db.Add(new SubscriptionPause { Id = Guid.NewGuid(), OrderId = order.Id,
            SubscriptionId = await db.Set<SubscriptionServiceDay>().Where(d => d.OrderId == order.Id).Select(d => d.SubscriptionId).FirstAsync(),
            PauseStartDate = today.AddDays(-1), ResumeDate = today.AddDays(3), RequestedResumeDate = today.AddDays(3), CreatedBy = userId,
            CreatedAt = clock.Now, UpdatedAt = clock.Now });
        var zeroId = Guid.NewGuid();
        db.CustomerProfiles.Add(new CustomerProfile { Id = zeroId, PreferredName = "No bookings", CreatedAt = clock.Now, UpdatedAt = clock.Now });
        await db.SaveChangesAsync();
        var service = new AdminCustomerService(db, clock);
        var customer = Assert.Single((await service.ListAsync("sArA", 1, 20, default)).Items);
        Assert.Equal("Sara Ali", customer.Name);
        Assert.Equal(7, customer.TotalOrders); Assert.Equal(4, customer.BookedPlans);
        Assert.Equal(1, customer.ActivePlans); Assert.Equal(1, customer.PausedPlans);
        Assert.Equal(1, customer.PendingOrders); Assert.Equal(1, customer.CancelledOrders); Assert.Equal(1, customer.RejectedOrders);
        Assert.Single((await service.ListAsync("55555555", 1, 20, default)).Items);
        Assert.Single((await service.ListAsync("SARA@EXAMPLE.TEST", 1, 20, default)).Items);
        var zero = (await service.GetAsync(zeroId, default))!;
        Assert.Equal(0, zero.BookedPlans); Assert.Equal(0, zero.TotalOrders); Assert.Null(zero.LatestOrderAt);
        var first = await service.ListAsync(null, 1, 1, default); var second = await service.ListAsync(null, 2, 1, default);
        Assert.Equal(2, first.TotalCount); Assert.NotEqual(first.Items[0].Id, second.Items[0].Id);
        Assert.Null(await service.GetAsync(Guid.NewGuid(), default));
    }

    [UpcomingPostgresFact]
    public async Task Customer_HTTP_history_is_scoped_and_menu_is_exposed_without_writing_menu_rows()
    {
        var setup = await ApprovalClient(); await using var factory = setup.Factory; using var client = setup.Client;
        var another = Guid.NewGuid();
        db.CustomerProfiles.Add(new CustomerProfile { Id = another, PreferredName = "Other customer", CreatedAt = clock.Now, UpdatedAt = clock.Now });
        await db.SaveChangesAsync();
        var menuCount = await db.Menus.CountAsync();
        var response = await client.GetAsync($"/api/admin/customers/{order.CustomerProfileId}/orders");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var history = (await response.Content.ReadFromJsonAsync<StaffOrderListResponse>())!;
        Assert.Single(history.Items); Assert.Equal(order.Id, history.Items[0].Order.Id);
        var empty = (await client.GetFromJsonAsync<StaffOrderListResponse>($"/api/admin/customers/{another}/orders"))!;
        Assert.Empty(empty.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/admin/customers/{Guid.NewGuid()}/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/customers?pageSize=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/customers?search=" + new string('x', 201))).StatusCode);
        var list = await client.GetFromJsonAsync<AdminCustomerListResponse>("/api/v1/admin/customers");
        Assert.Equal(2, list!.TotalCount);
        var adminRole = await db.Roles.SingleOrDefaultAsync(r => r.NormalizedName == "ADMIN");
        if (adminRole == null)
        {
            adminRole = new IdentityRole<Guid>("Admin") { Id = Guid.NewGuid(), NormalizedName = "ADMIN" };
            db.Roles.Add(adminRole);
        }
        if (!await db.UserRoles.AnyAsync(r => r.UserId == userId && r.RoleId == adminRole.Id))
            db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = userId, RoleId = adminRole.Id });
        await db.SaveChangesAsync();
        using var scope = factory.Services.CreateScope();
        var access = scope.ServiceProvider.GetRequiredService<DietTime.Application.IAccessControlService>();
        var screens = await access.GetUserScreensAsync(userId, default);
        var menu = Assert.Single(screens.Where(s => s.RouteUrl == "/customers"));
        Assert.True(menu.CanRead); Assert.False(menu.CanWrite);
        Assert.Equal(menuCount, await db.Menus.CountAsync());
    }
}
