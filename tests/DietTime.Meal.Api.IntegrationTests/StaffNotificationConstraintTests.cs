using DietTime.Application;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Legacy_notification_constraint_rejects_pause_and_schema_fix_restores_submission_and_projection()
    {
        var command = await ApprovalPauseCommand();
        var originalSchedule = await ApprovalSchedule(db);
        var historical = new StaffNotificationEvent { Id = Guid.NewGuid(), OrderId = order.Id,
            DeduplicationKey = "historical-order", EventType = "ORDER_RECEIVED", CreatedAt = clock.Now };
        db.Add(historical);
        var role = new IdentityRole<Guid>("Admin") { Id = Guid.NewGuid() };
        db.Roles.Add(role); db.UserRoles.Add(new() { UserId = userId, RoleId = role.Id });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE public.staff_notification_events DROP CONSTRAINT ck_staff_notification_event_type;
            ALTER TABLE public.staff_notification_events ADD CONSTRAINT ck_staff_notification_event_type
                CHECK (event_type IN ('ORDER_RECEIVED','ORDER_APPROVED','ORDER_REJECTED','ORDER_CANCELLED',
                    'DELIVERY_OVERDUE','DELIVERED','COLLECTED'));
            """);

        await using (var work = Context())
        {
            var error = await Assert.ThrowsAsync<DbUpdateException>(() =>
                Service(work).RequestPauseAsync(order.Id, userId, command, default));
            var postgres = Assert.IsType<PostgresException>(error.GetBaseException());
            Assert.Equal("23514", postgres.SqlState);
            Assert.Equal("staff_notification_events", postgres.TableName);
            Assert.Equal("ck_staff_notification_event_type", postgres.ConstraintName);
        }
        Assert.Empty(await db.Set<PackageChangeRequest>().AsNoTracking().ToArrayAsync());
        Assert.Equal(originalSchedule, await ApprovalSchedule(db));

        // Reproduce the manually applied correction inside this disposable database only.
        const string sql = """
            BEGIN;
            SET LOCAL lock_timeout = '5s';
            SET LOCAL statement_timeout = '60s';
            ALTER TABLE public.staff_notification_events DROP CONSTRAINT IF EXISTS ck_staff_notification_event_type;
            ALTER TABLE public.staff_notification_events ADD CONSTRAINT ck_staff_notification_event_type
                CHECK (event_type IN ('ORDER_RECEIVED','ORDER_APPROVED','ORDER_REJECTED','ORDER_CANCELLED',
                    'DELIVERY_OVERDUE','DELIVERED','COLLECTED',
                    'PACKAGE_PAUSE_REQUESTED','PACKAGE_RESUME_REQUESTED',
                    'PACKAGE_PAUSE_APPROVED','PACKAGE_RESUME_APPROVED',
                    'PACKAGE_PAUSE_REJECTED','PACKAGE_RESUME_REJECTED')) NOT VALID;
            ALTER TABLE public.staff_notifications DROP CONSTRAINT IF EXISTS ck_staff_notification_type;
            ALTER TABLE public.staff_notifications ADD CONSTRAINT ck_staff_notification_type
                CHECK (event_type IN ('ORDER_RECEIVED','ORDER_APPROVED','ORDER_REJECTED','ORDER_CANCELLED',
                    'DELIVERY_OVERDUE','DELIVERED','COLLECTED',
                    'PACKAGE_PAUSE_REQUESTED','PACKAGE_RESUME_REQUESTED',
                    'PACKAGE_PAUSE_APPROVED','PACKAGE_RESUME_APPROVED',
                    'PACKAGE_PAUSE_REJECTED','PACKAGE_RESUME_REJECTED')) NOT VALID;
            COMMIT;
            """;
        await db.Database.ExecuteSqlRawAsync(sql);
        await db.Database.ExecuteSqlRawAsync(sql);
        Assert.Equal(historical.CreatedAt, (await db.StaffNotificationEvents.AsNoTracking()
            .SingleAsync(e => e.Id == historical.Id)).CreatedAt);

        await using var fresh = Context();
        var service = Service(fresh);
        var pending = await service.RequestPauseAsync(order.Id, userId, command, default);
        Assert.Equal("PENDING_APPROVAL", pending.LatestRequest!.Status);
        await service.RequestPauseAsync(order.Id, userId, command, default);
        Assert.Single(await fresh.StaffNotificationEvents.Where(e => e.EventType == "PACKAGE_PAUSE_REQUESTED").ToArrayAsync());
        Assert.Equal(originalSchedule, await ApprovalSchedule(fresh));
        await service.ReviewChangeRequestAsync(command.CommandId, userId, new(true), default);
        // Reproduce the separate worker failure against the legacy inbox check.
        await fresh.Database.ExecuteSqlRawAsync("""
            ALTER TABLE public.staff_notifications DROP CONSTRAINT ck_staff_notification_type;
            ALTER TABLE public.staff_notifications ADD CONSTRAINT ck_staff_notification_type
                CHECK (event_type IN ('ORDER_RECEIVED','ORDER_APPROVED','ORDER_REJECTED','ORDER_CANCELLED',
                    'DELIVERY_OVERDUE','DELIVERED','COLLECTED'));
            """);
        await using (var worker = Context())
        {
            var projectionError = await Assert.ThrowsAsync<PostgresException>(() =>
                new StaffNotificationService(worker, clock).ProjectAsync(default));
            Assert.Equal("23514", projectionError.SqlState);
            Assert.Equal("ck_staff_notification_type", projectionError.ConstraintName);
        }
        Assert.Empty(await fresh.StaffNotifications.ToArrayAsync());
        Assert.All(await fresh.StaffNotificationEvents.AsNoTracking().ToArrayAsync(), e => Assert.Null(e.ProcessedAt));
        await fresh.Database.ExecuteSqlRawAsync(sql);
        await fresh.Database.ExecuteSqlRawAsync(sql);
        var inbox = new StaffNotificationService(fresh, clock);
        await inbox.ProjectAsync(default);
        await inbox.ProjectAsync(default);
        var notifications = (await inbox.ListAsync(userId, true, 1, 50, default)).Items;
        Assert.Single(notifications.Where(n => n.EventType == "PACKAGE_PAUSE_REQUESTED"));
        Assert.Single(notifications.Where(n => n.EventType == "PACKAGE_PAUSE_APPROVED"));

        // Exercise every supported type against the corrected constraint, including resume/rejection.
        foreach (var type in StaffNotificationRules.AdminEvents)
            fresh.Add(new StaffNotificationEvent { Id = Guid.NewGuid(), OrderId = order.Id,
                DeduplicationKey = "constraint-test:" + type, EventType = type, CreatedAt = clock.Now });
        await fresh.SaveChangesAsync();
        await inbox.ProjectAsync(default);
        foreach (var type in StaffNotificationRules.AdminEvents)
            Assert.True(await fresh.StaffNotifications.AnyAsync(n => n.EventType == type));
        fresh.Add(new StaffNotificationEvent { Id = Guid.NewGuid(), OrderId = order.Id,
            DeduplicationKey = "invalid-type", EventType = "INVALID", CreatedAt = clock.Now });
        var invalid = await Assert.ThrowsAsync<DbUpdateException>(() => fresh.SaveChangesAsync());
        Assert.Equal("ck_staff_notification_event_type", Assert.IsType<PostgresException>(invalid.GetBaseException()).ConstraintName);
    }
}
