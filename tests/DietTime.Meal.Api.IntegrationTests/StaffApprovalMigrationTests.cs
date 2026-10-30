using DietTime.Domain;
using DietTime.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace DietTime.Meal.Api.IntegrationTests;
public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Approval_migration_is_idempotent_and_retains_historical_status_and_manual_payment_policy()
    {
        await db.Database.ExecuteSqlRawAsync("""
            DROP TABLE public.order_notifications;
            ALTER TABLE public.orders DROP COLUMN requires_prepayment, DROP COLUMN approved_by, DROP COLUMN approved_at,
                DROP COLUMN rejected_by, DROP COLUMN rejected_at, DROP COLUMN rejection_reason, DROP COLUMN hold_reason, DROP COLUMN fulfillment_blocked_reason;
            ALTER TABLE public.order_status_history DROP COLUMN previous_status, DROP COLUMN changed_by, DROP COLUMN previous_payment_status, DROP COLUMN payment_status;
            """);
        var migrationSql=new StaffOrderApproval().UpOperations.OfType<SqlOperation>().Single().Sql;
        await db.Database.ExecuteSqlRawAsync("BEGIN;\n"+migrationSql+"\nCOMMIT;");
        db.ChangeTracker.Clear();
        var historical=await db.Orders.SingleAsync();Assert.Equal("CONFIRMED",historical.Status);Assert.False(historical.RequiresPrepayment);Assert.Null(historical.ApprovedBy);
        Assert.Equal("PENDING",historical.PaymentStatus);
        historical.RequiresPrepayment=true;historical.ApprovedBy=userId;await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("BEGIN;\n"+migrationSql+"\nCOMMIT;");
        historical=await db.Orders.AsNoTracking().SingleAsync();Assert.True(historical.RequiresPrepayment);Assert.Equal(userId,historical.ApprovedBy);Assert.Equal("CONFIRMED",historical.Status);
        Assert.Empty(await db.OrderNotifications.ToArrayAsync()); // Existing orders do not generate new acknowledgments.
        Assert.Single(await db.Menus.Where(m=>m.RouteUrl=="/orders").ToArrayAsync());
    }
}
