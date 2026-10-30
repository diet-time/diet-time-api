using DietTime.Domain;
using DietTime.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Takeaway_branch_migration_is_idempotent_and_preserves_existing_orders_and_configuration()
    {
        var original = await db.Orders.AsNoTracking().SingleAsync();
        // Exercise an existing orders schema before the branch migration.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE public.orders DROP CONSTRAINT fk_orders_takeaway_branches; ALTER TABLE public.orders DROP COLUMN takeaway_branch_id, DROP COLUMN takeaway_branch_name, DROP COLUMN takeaway_branch_address; DROP TABLE public.takeaway_branches;");
        var sql = new AddTakeawayBranches().UpOperations.OfType<SqlOperation>().Single().Sql;
        await db.Database.ExecuteSqlRawAsync("BEGIN;\n" + sql + "\nCOMMIT;");
        db.ChangeTracker.Clear();
        var branches = await db.TakeawayBranches.OrderBy(x => x.Name).ToArrayAsync();
        Assert.Equal(2, branches.Length);
        Assert.Contains(branches, x => x.Name == "Diet Time – Izghawa Branch");
        Assert.Contains(branches, x => x.Name == "Diet Time – Azizia Branch");
        Assert.All(branches, x => Assert.Null(x.Address));
        branches[0].Address = "Administrator configured address";
        branches[0].IsActive = false;
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("BEGIN;\n" + sql + "\nCOMMIT;");
        var configured = await db.TakeawayBranches.AsNoTracking().SingleAsync(x => x.Id == branches[0].Id);
        Assert.False(configured.IsActive);
        Assert.Equal("Administrator configured address", configured.Address);
        Assert.Equal(2, await db.TakeawayBranches.CountAsync());
        var historical = await db.Orders.AsNoTracking().SingleAsync();
        Assert.Equal(original.Status, historical.Status);
        Assert.Equal(original.StartDate, historical.StartDate);
        Assert.Null(historical.TakeawayBranchId);
        Assert.Null(historical.TakeawayBranchName);
    }
}
