using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Existing_pending_package_requests_are_recovered_without_duplicate_notifications()
    {
        var role = new IdentityRole<Guid>("Admin") { Id = Guid.NewGuid() };
        db.Roles.Add(role); db.UserRoles.Add(new() { UserId = userId, RoleId = role.Id });
        var request = new PackageChangeRequest { Id = Guid.NewGuid(), OrderId = order.Id,
            Type = "PAUSE", CreatedAt = clock.Now, RequestedBy = userId };
        db.Add(request); await db.SaveChangesAsync();
        // Simulate a request persisted by the earlier version, which did not capture an event.
        await db.StaffNotificationEvents.Where(e => e.OrderId == order.Id).ExecuteDeleteAsync();
        var inbox = new StaffNotificationService(db, clock);
        await inbox.ProjectAsync(default); await inbox.ProjectAsync(default);
        var row = Assert.Single((await inbox.ListAsync(userId, true, 1, 50, default)).Items);
        Assert.Equal("PACKAGE_PAUSE_REQUESTED", row.EventType);
        Assert.Equal(1, await inbox.UnreadAsync(userId, true, default));
    }

    private AdminMealService DurationAdmin() => new(db, clock, new MemoryCache(new MemoryCacheOptions()),
        Options.Create(new DeliveryScheduleOptions()), Options.Create(new MenuRotationOptions()), new TestStorage());

    [UpcomingPostgresFact]
    public async Task Duration_edit_updates_linked_and_legacy_prices_but_preserves_booked_service_days()
    {
        await PrepareAsync();
        var before = await db.Set<SubscriptionServiceDay>().AsNoTracking().Where(d => d.OrderId == order.Id)
            .OrderBy(d => d.ServiceDate).Select(d => new { d.Id, d.ServiceDate }).ToArrayAsync();
        var originalEnd = order.EndDate;
        var legacy = new MealPlanPrice { MealPlanTemplateId = order.MealPlanTemplateId,
            DurationDays = 12, MealsPerDay = 1, Amount = 500 };
        db.Add(new MealPlanPricePackage { Code = "OTHER", NameEn = "Other", NameAr = "Other", DurationDays = 12, DisplayOrder = 10 });
        var unrelated = new MealPlanPrice { MealPlanTemplateId = order.MealPlanTemplateId,
            PackageCode = "OTHER", DurationDays = 12, MealsPerDay = 2, Amount = 600 };
        db.AddRange(legacy, unrelated); await db.SaveChangesAsync();
        var result = await DurationAdmin().UpdateMealPlanPricePackageAsync("FORTNIGHT",
            new("FORTNIGHT", "Two weeks", "Two weeks", 13, 0, true), userId, default);
        Assert.Equal(MealPlanPricePackageWriteResult.Success, result);
        await using var fresh = Context();
        Assert.Equal(13, (await fresh.MealPlanPricePackages.SingleAsync(p => p.Code == "FORTNIGHT")).DurationDays);
        var price = await fresh.MealPlanPrices.SingleAsync(p => p.Id == order.MealPlanPriceId);
        Assert.Equal(13, price.DurationDays); Assert.Equal(1299, price.Amount);
        Assert.Equal(13, (await fresh.MealPlanPrices.SingleAsync(p => p.Id == legacy.Id)).DurationDays);
        Assert.Equal(12, (await fresh.MealPlanPrices.SingleAsync(p => p.Id == unrelated.Id)).DurationDays);
        Assert.Equal(originalEnd, (await fresh.Orders.SingleAsync(o => o.Id == order.Id)).EndDate);
        await Service().InitializeAsync(order, default);
        var after = await fresh.Set<SubscriptionServiceDay>().AsNoTracking().Where(d => d.OrderId == order.Id)
            .OrderBy(d => d.ServiceDate).Select(d => new { d.Id, d.ServiceDate }).ToArrayAsync();
        Assert.Equal(before, after);
    }

    [UpcomingPostgresFact]
    public async Task Conflicting_duration_edit_rolls_back_package_and_prices()
    {
        db.Add(new MealPlanPrice { MealPlanTemplateId = order.MealPlanTemplateId,
            DurationDays = 13, MealsPerDay = 3, SnacksPerDay = 1, Amount = 1400 });
        await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<MealConfigurationException>(() => DurationAdmin().UpdateMealPlanPricePackageAsync(
            "FORTNIGHT", new("FORTNIGHT", "Changed", "Changed", 13, 0, true), userId, default));
        Assert.Equal("package_duration_conflict", error.Code);
        await using var fresh = Context();
        Assert.Equal(12, (await fresh.MealPlanPricePackages.SingleAsync(p => p.Code == "FORTNIGHT")).DurationDays);
        Assert.Equal(12, (await fresh.MealPlanPrices.SingleAsync(p => p.Id == order.MealPlanPriceId)).DurationDays);
    }

    [UpcomingPostgresFact]
    public async Task Pause_and_resume_requests_and_approvals_reach_admin_inbox_once()
    {
        await PrepareAsync();
        var role = new IdentityRole<Guid>("Admin") { Id = Guid.NewGuid() };
        db.Roles.Add(role); db.UserRoles.Add(new() { UserId = userId, RoleId = role.Id }); await db.SaveChangesAsync();
        var service = Service();
        var current = await service.GetPackageAsync(order.Id, userId, default);
        var start = current.EligiblePauseDates.First(); var restart = start.AddDays(14);
        var preview = await service.PreviewPauseAsync(order.Id, userId, new(start, restart), default);
        var pause = new PausePackageRequest(Guid.NewGuid(), preview.RowVersion, preview.ScheduleVersion, start, restart, "Travel");
        await service.RequestPauseAsync(order.Id, userId, pause, default);
        await service.RequestPauseAsync(order.Id, userId, pause, default);
        await service.ReviewChangeRequestAsync(pause.CommandId, userId, new(true, null), default);
        var paused = await service.GetPackageAsync(order.Id, userId, default);
        var resume = new ResumePackageRequest(Guid.NewGuid(), pause.CommandId, paused.RowVersion);
        await service.RequestResumeAsync(order.Id, userId, resume, default);
        await service.RequestResumeAsync(order.Id, userId, resume, default);
        await service.ReviewChangeRequestAsync(resume.CommandId, userId, new(true, null), default);
        var inbox = new StaffNotificationService(db, clock);
        await inbox.ProjectAsync(default); await inbox.ProjectAsync(default);
        var rows = (await inbox.ListAsync(userId, true, 1, 50, default)).Items;
        foreach (var type in new[] { "PACKAGE_PAUSE_REQUESTED", "PACKAGE_PAUSE_APPROVED", "PACKAGE_RESUME_REQUESTED", "PACKAGE_RESUME_APPROVED" })
            Assert.Single(rows.Where(n => n.EventType == type));
        Assert.Equal(4, await inbox.UnreadAsync(userId, true, default));
    }
}
