using System.Net;
using DietTime.Application;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Legacy_history_constraint_reproduces_23514_and_corrected_schema_restores_order_review()
    {
        var setup = await ApprovalClient(); await using var factory = setup.Factory; using var client = setup.Client;
        var historical = new OrderStatusHistory { Id = Guid.NewGuid(), OrderId = order.Id,
            Status = "PAUSED", Notes = "Existing historical audit", ChangedAt = clock.Now };
        db.Add(historical); await db.SaveChangesAsync();
        // The actual legacy constraint from database/scripts/pg_dump.sql.
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE public.order_status_history DROP CONSTRAINT IF EXISTS ck_order_status_history_status;
            ALTER TABLE public.order_status_history ADD CONSTRAINT ck_order_status_history_status
                CHECK (status IN ('PENDING','CONFIRMED','ACTIVE','PAUSED','COMPLETED','CANCELLED'));
            """);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => scope.ServiceProvider
                .GetRequiredService<IOrderService>().PlaceAsync(setup.Request, Guid.NewGuid().ToString(), userId, default));
            var postgres = Assert.IsType<PostgresException>(error.GetBaseException());
            Assert.Equal("23514", postgres.SqlState);
            Assert.Equal("order_status_history", postgres.TableName);
            Assert.Equal("ck_order_status_history_status", postgres.ConstraintName);
        }
        Assert.Single(await db.Orders.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.OrderNotifications.AsNoTracking().ToArrayAsync());

        // Recreate the database correction applied manually, without a migration-file dependency.
        const string sql = """
            SET LOCAL lock_timeout = '5s';
            SET LOCAL statement_timeout = '60s';
            ALTER TABLE public.order_status_history DROP CONSTRAINT IF EXISTS ck_order_status_history_status;
            ALTER TABLE public.order_status_history ADD CONSTRAINT ck_order_status_history_status
                CHECK (status IN ('PENDING_APPROVAL','ON_HOLD','CONFIRMED','REJECTED','CANCELLED',
                    'ACTIVE','COMPLETED','PENDING','PAUSED','PENDING_ACTIVATION')) NOT VALID;
            """;
        for (var run = 0; run < 2; run++)
            await db.Database.ExecuteSqlRawAsync("BEGIN;\n" + sql + "\nCOMMIT;");
        var savedHistory = await db.OrderStatusHistory.AsNoTracking().SingleAsync(h => h.Id == historical.Id);
        Assert.Equal("PAUSED", savedHistory.Status); Assert.Equal(historical.Notes, savedHistory.Notes);

        var pending = (await SubmitApprovalOrder(client, setup.Request)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Review(client, pending, "hold", 1, "Please confirm pickup")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Review(client, pending, "resume", 2)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Review(client, pending, "reject", 3, "Customer requested another package")).StatusCode);
        Assert.Equal(new[] { "ON_HOLD", "PENDING_APPROVAL", "PENDING_APPROVAL", "REJECTED" },
            await db.OrderStatusHistory.AsNoTracking().Where(h => h.OrderId == pending).Select(h => h.Status).OrderBy(status => status).ToArrayAsync());

        var approved = (await SubmitApprovalOrder(client, setup.Request)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Review(client, approved, "approve", 1)).StatusCode);
        Assert.Equal("CONFIRMED", (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == approved)).Status);
        Assert.Equal(12, await db.Set<SubscriptionServiceDay>().CountAsync(d => d.OrderId == approved));
        Assert.Equal("CONFIRMED", (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
    }
}
