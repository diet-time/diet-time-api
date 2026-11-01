using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Persistence;

public sealed class ServiceDayFulfillmentService(DietTimeDbContext db, TimeProvider clock)
{
    public async Task<ServiceDayFulfillmentResponse> UpdateAsync(Guid id, string? status, Guid? actor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var orderId = await db.Set<SubscriptionServiceDay>().Where(d => d.Id == id).Select(d => d.OrderId).SingleOrDefaultAsync(ct);
        if (orderId is null) throw new FulfillmentException(404, "service_day_not_found", "The service day or its linked order was not found.");
        // Match staff approval/cancellation lock order, then serialize duplicate completion requests.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM public.orders WHERE id = {orderId.Value} FOR UPDATE", ct);
        var order = await db.Orders.Include(o => o.DeliveryDays).SingleAsync(o => o.Id == orderId.Value, ct);
        await db.Entry(order).ReloadAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM public.subscription_service_days WHERE id = {id} FOR UPDATE", ct);
        var day = await db.Set<SubscriptionServiceDay>().SingleAsync(d => d.Id == id, ct);
        await db.Entry(day).ReloadAsync(ct);
        if (ServiceDayFulfillment.Apply(day, order, status, clock.GetUtcNow(), actor)) await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        var staffName = day.FulfilledBy is { } staff ? await db.UserProfiles.AsNoTracking().Where(p => p.UserId == staff)
            .Select(p => (p.FirstName + " " + p.LastName).Trim()).FirstOrDefaultAsync(ct) : null;
        return new(day.Id, day.Status, day.FulfilledAt, day.FulfilledBy, string.IsNullOrWhiteSpace(staffName) ? null : staffName);
    }
}
