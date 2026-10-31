using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DietTime.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Daily_staff_orders_include_unapproved_purchases_by_service_date_before_pagination()
    {
        var setup = await ApprovalClient();
        await using var factory = setup.Factory;
        using var client = setup.Client;
        var first = (await SubmitApprovalOrder(client, setup.Request)).GetProperty("id").GetGuid();
        var second = (await SubmitApprovalOrder(client, setup.Request with { StartDate = new(2026, 11, 3) }))
            .GetProperty("id").GetGuid();
        Assert.False(await db.Set<SubscriptionServiceDay>().AnyAsync(d => d.OrderId == first || d.OrderId == second));

        var day = await client.GetFromJsonAsync<JsonElement>("/api/admin/orders?status=PENDING_APPROVAL&serviceDate=2026-10-27");
        Assert.Equal(1, day.GetProperty("totalCount").GetInt32());
        Assert.Equal(first, Assert.Single(day.GetProperty("items").EnumerateArray()).GetProperty("order").GetProperty("id").GetGuid());
        foreach (var date in new[] { "2026-10-26", "2026-10-30", "2040-01-01" })
        {
            var empty = await client.GetFromJsonAsync<JsonElement>($"/api/admin/orders?status=PENDING_APPROVAL&serviceDate={date}");
            Assert.Equal(0, empty.GetProperty("totalCount").GetInt32());
        }
        var pageOne = await client.GetFromJsonAsync<JsonElement>("/api/admin/orders?status=PENDING_APPROVAL&serviceDate=2026-11-03&pageSize=1&pageNumber=1");
        var pageTwo = await client.GetFromJsonAsync<JsonElement>("/api/admin/orders?status=PENDING_APPROVAL&serviceDate=2026-11-03&pageSize=1&pageNumber=2");
        Assert.Equal(2, pageOne.GetProperty("totalCount").GetInt32());
        var pageOneId = Assert.Single(pageOne.GetProperty("items").EnumerateArray()).GetProperty("order").GetProperty("id").GetGuid();
        var pageTwoId = Assert.Single(pageTwo.GetProperty("items").EnumerateArray()).GetProperty("order").GetProperty("id").GetGuid();
        Assert.NotEqual(pageOneId, pageTwoId);
        Assert.Contains(first, new[] { pageOneId, pageTwoId });
        Assert.Contains(second, new[] { pageOneId, pageTwoId });
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/orders?serviceDate=not-a-date")).StatusCode);

        (await Review(client, first, "approve", 1)).EnsureSuccessStatusCode();
        day = await client.GetFromJsonAsync<JsonElement>("/api/admin/orders?serviceDate=2026-10-27");
        Assert.Contains(day.GetProperty("items").EnumerateArray(), row => row.GetProperty("order").GetProperty("id").GetGuid() == first &&
            row.GetProperty("order").GetProperty("status").GetString() == "CONFIRMED");
        Assert.DoesNotContain(day.GetProperty("items").EnumerateArray(), row => row.GetProperty("order").GetProperty("id").GetGuid() == second);
    }

    [UpcomingPostgresFact]
    public async Task Administrators_receive_order_navigation_when_the_legacy_menu_catalogue_has_no_order_entry()
    {
        var setup = await ApprovalClient();
        await using var factory = setup.Factory;
        using var client = setup.Client;
        var role = new IdentityRole<Guid>("Admin") { Id = Guid.NewGuid(), NormalizedName = "ADMIN" };
        db.Roles.Add(role);
        db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = userId, RoleId = role.Id });
        await db.SaveChangesAsync();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var screens = await client.GetFromJsonAsync<JsonElement>("/api/v1/access-control/me/screens");
            var orders = Assert.Single(screens.GetProperty("data").EnumerateArray(), s => s.GetProperty("routeUrl").GetString() == "/orders");
            Assert.True(orders.GetProperty("canRead").GetBoolean());
            Assert.True(orders.GetProperty("canWrite").GetBoolean());
        }
        Assert.False(await db.Menus.AnyAsync(m => m.RouteUrl == "/orders")); // No database repair/migration is needed.

        var configured = new Menu { Id = Guid.NewGuid(), MainMenuCode = "OPERATIONS", MainMenuName = "Operations",
            SubMenuCode = "ORDERS", SubMenuName = "Orders", RouteUrl = "/orders", IsActive = false };
        db.Menus.Add(configured);
        await db.SaveChangesAsync();
        var inactive = await client.GetFromJsonAsync<JsonElement>("/api/v1/access-control/me/screens");
        Assert.DoesNotContain(inactive.GetProperty("data").EnumerateArray(), s => s.GetProperty("routeUrl").GetString() == "/orders");
        configured.IsActive = true;
        await db.SaveChangesAsync();
        var active = await client.GetFromJsonAsync<JsonElement>("/api/v1/access-control/me/screens");
        Assert.Equal(configured.Id, Assert.Single(active.GetProperty("data").EnumerateArray(),
            s => s.GetProperty("routeUrl").GetString() == "/orders").GetProperty("screenId").GetGuid());
    }

    [UpcomingPostgresFact]
    public async Task Missing_menu_fallback_does_not_grant_order_access_to_non_administrators()
    {
        var setup = await ApprovalClient();
        await using var factory = setup.Factory;
        using var client = setup.Client;
        var screens = await client.GetFromJsonAsync<JsonElement>("/api/v1/access-control/me/screens");
        Assert.DoesNotContain(screens.GetProperty("data").EnumerateArray(), s => s.GetProperty("routeUrl").GetString() == "/orders");
    }
}
