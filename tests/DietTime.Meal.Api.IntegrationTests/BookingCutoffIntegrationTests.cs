using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task New_next_day_bookings_are_rejected_at_cutoff_and_customer_dates_advance()
    {
        var fields = await BookingFieldsAsync();
        var queue = new BookingQueue();
        var service = BookingService(db, queue);
        foreach (var minute in new[] { 0, 30 })
        {
            clock.Now = new(2026, 10, 26, 18, minute, 0, TimeSpan.Zero);
            var rejected = await service.PlaceAsync(BookingRequest(fields, new(2026, 10, 27)), Guid.NewGuid().ToString(), userId, default);
            Assert.Equal(PlaceOrderStatus.InvalidStartDate, rejected.Status);
        }
        Assert.Equal(1, await db.Orders.CountAsync()); // Seed order only.
        Assert.Equal(0, queue.Count);
        var customer = new CustomerMealPlanPurchaseService(db, new TestStorage(), clock, Options.Create(new OrderOptions()));
        var purchase = await customer.GetPurchaseOptionsAsync("BALANCE", userId, "en", default);
        Assert.All(purchase!.MealConfigurations.SelectMany(c => c.Packages), p => Assert.Equal(new DateOnly(2026, 10, 28), p.EarliestStartDate));
        var later = await service.PlaceAsync(BookingRequest(fields, new(2026, 10, 28)), Guid.NewGuid().ToString(), userId, default);
        Assert.Equal(PlaceOrderStatus.Created, later.Status);
    }

    [UpcomingPostgresFact]
    public async Task A_pre_cutoff_order_can_be_replayed_after_cutoff_without_a_new_booking()
    {
        var fields = await BookingFieldsAsync();
        clock.Now = new(2026, 10, 26, 17, 59, 59, TimeSpan.Zero);
        var queue = new BookingQueue();
        var service = BookingService(db, queue);
        var key = Guid.NewGuid().ToString();
        var request = BookingRequest(fields, new(2026, 10, 27));
        Assert.Equal(PlaceOrderStatus.Created, (await service.PlaceAsync(request, key, userId, default)).Status);
        clock.Now = new(2026, 10, 26, 18, 0, 0, TimeSpan.Zero);
        Assert.Equal(PlaceOrderStatus.Replayed, (await service.PlaceAsync(request, key, userId, default)).Status);
        Assert.Equal(2, await db.Orders.CountAsync());
        Assert.Equal(1, queue.Count);
    }

    [UpcomingPostgresFact]
    public async Task Crossing_cutoff_during_database_save_rolls_back_the_new_order()
    {
        var fields = await BookingFieldsAsync();
        clock.Now = new(2026, 10, 26, 17, 59, 59, TimeSpan.Zero);
        await using var context = new DietTimeDbContext(new DbContextOptionsBuilder<DietTimeDbContext>()
            .UseNpgsql(connection).UseSnakeCaseNamingConvention()
            .AddInterceptors(new BookingClockInterceptor(() => clock.Now = new(2026, 10, 26, 18, 0, 0, TimeSpan.Zero)))
            .Options);
        var queue = new BookingQueue();
        var result = await BookingService(context, queue).PlaceAsync(BookingRequest(fields, new(2026, 10, 27)), Guid.NewGuid().ToString(), userId, default);
        Assert.Equal(PlaceOrderStatus.InvalidStartDate, result.Status);
        Assert.Equal(1, await db.Orders.CountAsync());
        Assert.Equal(0, queue.Count);
    }

    private OrderService BookingService(DietTimeDbContext context, BookingQueue queue) =>
        new(context, clock, Options.Create(new OrderOptions()), queue, NullLogger<OrderService>.Instance);
    private PlaceOrderRequest BookingRequest((Guid Address, Guid Slot) fields, DateOnly start) =>
        new(order.CustomerProfileId, order.MealPlanTemplateId, order.MealPlanPriceId, fields.Address, fields.Slot, start, [1, 2, 3, 4, 6, 7]);
    private async Task<(Guid Address, Guid Slot)> BookingFieldsAsync()
    {
        var address = new CustomerAddress { Id = Guid.NewGuid(), CustomerProfileId = order.CustomerProfileId, AddressType = "HOME", Area = "Doha", IsActive = true };
        var slot = new DeliveryTimeSlot { Id = Guid.NewGuid(), Code = "BOOKING", Name = "Morning", NameAr = "Morning", StartTime = new(7,0), EndTime = new(11,0), IsActive = true };
        db.AddRange(address, slot);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE SEQUENCE IF NOT EXISTS public.order_number_seq");
        return (address.Id, slot.Id);
    }
    private sealed class BookingQueue : IOrderNotificationQueue
    {
        public int Count { get; private set; }
        public bool TryEnqueue(Guid orderId) { Count++; return true; }
    }
    private sealed class BookingClockInterceptor(Action crossCutoff) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Order>().Any(e => e.State == EntityState.Added)) crossCutoff();
            return ValueTask.FromResult(result);
        }
    }
}