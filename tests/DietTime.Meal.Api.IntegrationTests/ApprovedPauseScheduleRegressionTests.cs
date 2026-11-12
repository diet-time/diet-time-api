using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Approved_full_pause_publishes_December_service_dates_and_paused_status_everywhere()
    {
        clock.Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
        order.StartDate = new(2026, 11, 14); order.EndDate = new(2026, 12, 10);
        await db.SaveChangesAsync();
        var service = Service(); await service.InitializeAsync(order, default);
        var original = await db.Set<SubscriptionServiceDay>().Where(d => d.OrderId == order.Id).OrderBy(d => d.Id).Select(d => d.Id).ToArrayAsync();
        Assert.Equal(24, original.Length);
        var restart = new DateOnly(2026, 12, 1);
        var preview = await service.PreviewPauseAsync(order.Id, userId, new(order.StartDate, restart), default);
        var command = new PausePackageRequest(Guid.NewGuid(), preview.RowVersion, preview.ScheduleVersion, order.StartDate, restart, "Travel");
        await service.RequestPauseAsync(order.Id, userId, command, default);
        Assert.Equal("PAUSE_REQUESTED", (await service.GetPackageAsync(order.Id, userId, default)).DisplayStatus);
        await service.ReviewChangeRequestAsync(command.CommandId, Guid.NewGuid(), new(true), default);
        var package = await service.GetPackageAsync(order.Id, userId, default);
        Assert.Equal("PAUSED", package.DisplayStatus);
        Assert.Equal(restart, package.NextScheduledDelivery);
        Assert.Equal(new DateOnly(2026, 12, 28), package.ExpectedEndDate);
        Assert.Equal(24, package.RemainingServiceDays);
        // The original booking date remains available in storage; responses use real service rows.
        Assert.Equal(new DateOnly(2026, 11, 14), (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).StartDate);
        var actual = await db.Set<SubscriptionServiceDay>().AsNoTracking().Where(d => d.OrderId == order.Id).OrderBy(d => d.ServiceDate).Select(d => d.ServiceDate).ToArrayAsync();
        Assert.Equal(24, actual.Length); Assert.All(actual, date => Assert.True(date >= restart));
        var customers = new OrderService(db, clock, Options.Create(new OrderOptions()), new BookingQueue(), NullLogger<OrderService>.Instance);
        var detail = (await customers.GetAsync(order.Id, userId, default))!;
        Assert.Equal(restart, detail.Delivery.StartDate);
        Assert.Equal(actual, detail.Delivery.ServiceDates);
        var summary = (await customers.GetDashboardAsync(order.CustomerProfileId, userId, default))!.Items.Single(o => o.Id == order.Id);
        Assert.Equal(restart, summary.StartDate); Assert.Equal("PAUSED", summary.PackageSchedule!.DashboardStatus);
        var staff = new StaffOrderService(db, clock, Options.Create(new OrderOptions()), service, new BookingQueue());
        var staffDetail = (await staff.GetAsync(order.Id, default))!;
        Assert.Equal(restart, staffDetail.Order.Delivery.StartDate);
        Assert.Equal(actual, staffDetail.Order.Delivery.ServiceDates);
        Assert.Equal("PAUSED", staffDetail.Order.PackageSchedule!.DisplayStatus);
        var list = await staff.ListAsync(null, 1, 20, default);
        Assert.Equal(actual, list.Items.Single(o => o.Order.Id == order.Id).Order.Delivery.ServiceDates);
        Assert.Empty((await staff.ListAsync(null, 1, 20, default, new(2026, 11, 14))).Items);
        Assert.Single((await staff.ListAsync(null, 1, 20, default, restart)).Items);
        Assert.Single((await staff.ListAsync("PAUSED", 1, 20, default)).Items);
        // Reinitializing must never regenerate the old November deliveries.
        await service.InitializeAsync(order, default);
        Assert.Equal(original, await db.Set<SubscriptionServiceDay>().Where(d => d.OrderId == order.Id).OrderBy(d => d.Id).Select(d => d.Id).ToArrayAsync());
        clock.Now = new(2026, 12, 1, 7, 0, 0, TimeSpan.Zero);
        Assert.Equal("ACTIVE", (await service.GetPackageAsync(order.Id, userId, default)).DisplayStatus);
        Assert.Empty((await staff.ListAsync("PAUSED", 1, 20, default)).Items);
    }
}
