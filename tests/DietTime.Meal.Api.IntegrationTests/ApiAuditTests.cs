using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Api_audit_records_flutter_reads_order_creation_and_react_approval_with_real_identity()
    {
        var profile = await db.CustomerProfiles.SingleAsync(p => p.Id == order.CustomerProfileId);
        profile.PreferredName = "Audit customer";
        await db.SaveChangesAsync();
        var setup = await ApprovalClient();
        await using var factory = setup.Factory;
        using var client = setup.Client;
        client.DefaultRequestHeaders.Add("X-Client-App", "FLUTTER");
        (await client.GetAsync($"/api/v1/orders/{order.Id}?password=must-not-be-recorded")).EnsureSuccessStatusCode();
        var read = await db.AuditEvents.AsNoTracking().Include(e => e.Targets).SingleAsync(e => e.EntityType == "Orders" && e.Action == "Get");
        Assert.Equal(userId, read.ActorUserId);
        Assert.Equal("Audit customer", read.ActorName);
        Assert.Equal("FLUTTER", read.Source);
        Assert.Equal(clock.Now, read.OccurredAt);
        Assert.Equal("SUCCEEDED", read.Outcome);
        Assert.Equal(200, read.StatusCode);
        Assert.Contains(read.Targets, t => t.TargetId == order.Id.ToString());
        Assert.DoesNotContain("password", read.Endpoint);

        var id = (await SubmitApprovalOrder(client, setup.Request)).GetProperty("id").GetGuid();
        var placed = await db.AuditEvents.AsNoTracking().Include(e => e.Targets).SingleAsync(e => e.Action == "Place");
        Assert.Equal(201, placed.StatusCode);
        Assert.Contains(placed.Targets, t => t.TargetId == id.ToString());
        client.DefaultRequestHeaders.Remove("X-Client-App");
        client.DefaultRequestHeaders.Add("X-Client-App", "REACT_ADMIN");
        Assert.Equal(HttpStatusCode.BadRequest, (await Review(client, id, "hold", 1, "")).StatusCode);
        var rejected = await db.AuditEvents.AsNoTracking().SingleAsync(e => e.Action == "Hold");
        Assert.Equal("REJECTED", rejected.Outcome);
        Assert.Equal(400, rejected.StatusCode);
        var spoofed = Guid.NewGuid();
        (await client.PostAsJsonAsync($"/api/admin/orders/{id}/approve", new { expectedRowVersion = 1,
            reason = "SECRET_NOTE_DO_NOT_CAPTURE", changedBy = spoofed })).EnsureSuccessStatusCode();
        var approval = await db.AuditEvents.AsNoTracking().Include(e => e.Targets).SingleAsync(e => e.Action == "Approve");
        Assert.Equal(userId, approval.ActorUserId);
        Assert.NotEqual(spoofed, approval.ActorUserId);
        Assert.Equal("REACT_ADMIN", approval.Source);
        Assert.Contains(approval.Targets, t => t.TargetId == id.ToString());
        var serialized = JsonSerializer.Serialize(await db.AuditEvents.AsNoTracking().Include(e => e.Targets)
            .Select(e => new { e.ActorUserId, e.ActorName, e.Endpoint, e.Action, targets = e.Targets.Select(t => t.TargetId) }).ToArrayAsync());
        Assert.DoesNotContain("SECRET_NOTE_DO_NOT_CAPTURE", serialized);
        Assert.DoesNotContain("must-not-be-recorded", serialized);
    }

    [UpcomingPostgresFact]
    public async Task Rejected_sign_in_does_not_store_credentials_and_customers_cannot_read_audit_history()
    {
        var setup = await ApprovalClient();
        await using var factory = setup.Factory;
        using var client = setup.Client;
        client.DefaultRequestHeaders.Add("X-Client-App", "REACT_ADMIN");
        client.DefaultRequestHeaders.Authorization = new("Bearer", "invalid-token-must-not-be-stored");
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new {
            email = "private-login@example.test", password = "SECRET_PASSWORD_NOT_FOR_AUDIT" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        var entry = await db.AuditEvents.AsNoTracking().SingleAsync(e => e.Action == "Login");
        Assert.Null(entry.ActorUserId);
        Assert.Equal("REJECTED", entry.Outcome);
        var metadata = JsonSerializer.Serialize(new { entry.ActorName, entry.Endpoint, entry.CorrelationId, entry.Action });
        Assert.DoesNotContain("private-login", metadata);
        Assert.DoesNotContain("SECRET_PASSWORD", metadata);
        Assert.DoesNotContain("invalid-token", metadata);
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(
            new System.IdentityModel.Tokens.Jwt.JwtSecurityToken("DietTime.Tests", "DietTime.Tests",
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, userId.ToString()),
                 new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Customer")],
                expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new Microsoft.IdentityModel.Tokens.SigningCredentials(
                    new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes("test-only-key-at-least-thirty-two-characters-long")),
                    Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256)));
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/audit")).StatusCode);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.EntityType == "Audit" && e.StatusCode == 403 && e.ActorUserId == userId));
    }

    [UpcomingPostgresFact]
    public async Task Audit_history_filters_users_sources_and_qatar_dates_before_pagination()
    {
        var setup = await ApprovalClient();
        await using var factory = setup.Factory;
        using var client = setup.Client;
        foreach (var utc in new[] { "2026-10-26T20:59:59Z", "2026-10-26T21:00:00Z", "2026-10-27T20:59:59Z", "2026-10-27T21:00:00Z" })
            db.AuditEvents.Add(new AuditEvent { Id = Guid.NewGuid(), ActorUserId = userId, ActorName = "Audit customer", Source = "FLUTTER",
                EntityType = "BoundaryTest", Action = "Read", HttpMethod = "GET", Endpoint = "api/test", StatusCode = 200,
                Outcome = "SUCCEEDED", CorrelationId = "test", OccurredAt = DateTimeOffset.Parse(utc) });
        await db.SaveChangesAsync();
        var path = $"/api/admin/audit?entityType=BoundaryTest&action=Read&source=FLUTTER&userId={userId}&from=2026-10-27&until=2026-10-27&pageSize=1";
        var first = await client.GetFromJsonAsync<JsonElement>(path + "&page=1");
        var second = await client.GetFromJsonAsync<JsonElement>(path + "&page=2");
        Assert.Equal(2, first.GetProperty("totalCount").GetInt32());
        Assert.Single(first.GetProperty("items").EnumerateArray());
        Assert.NotEqual(first.GetProperty("items")[0].GetProperty("id").GetGuid(), second.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Equal("Audit customer", first.GetProperty("items")[0].GetProperty("changedBy").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/audit?from=invalid")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/audit?from=2026-11-01&until=2026-10-01")).StatusCode);
    }

    [UpcomingPostgresFact]
    public async Task Audit_storage_failure_does_not_block_orders()
    {
        var setup = await ApprovalClient();
        await using var factory = setup.Factory;
        using var client = setup.Client;
        await db.Database.ExecuteSqlRawAsync("DROP TABLE public.audit_event_targets; DROP TABLE public.audit_events;");
        var id = (await SubmitApprovalOrder(client, setup.Request)).GetProperty("id").GetGuid();
        Assert.True(await db.Orders.AnyAsync(o => o.Id == id));
    }
}
