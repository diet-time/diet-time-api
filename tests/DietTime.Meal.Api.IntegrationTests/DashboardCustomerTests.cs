using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.Extensions.Options;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Customer_dashboard_counts_purchases_once_excludes_unaccepted_orders_and_respects_Qatar_date()
    {
        var date = new DateOnly(2026, 10, 27);
        order.Status = "CONFIRMED"; order.PlacedAt = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
        var beta = new CustomerProfile { Id = Guid.NewGuid(), PreferredName = "Zara repeat customer", CreatedAt = clock.Now };
        var boundary = new CustomerProfile { Id = Guid.NewGuid(), PreferredName = "Boundary customer", CreatedAt = clock.Now };
        var excluded = new CustomerProfile { Id = Guid.NewGuid(), PreferredName = "Excluded customer", CreatedAt = clock.Now };
        db.CustomerProfiles.AddRange(beta, boundary, excluded);
        void Booking(Guid customer, string status, DateTimeOffset placed)
        {
            db.Orders.Add(new Order { Id = Guid.NewGuid(), CustomerProfileId = customer, OrderNumber = "TEST-" + Guid.NewGuid().ToString("N")[..20],
                IdempotencyKey = Guid.NewGuid().ToString(), MealPlanTemplateId = order.MealPlanTemplateId, MealPlanPriceId = order.MealPlanPriceId,
                PlanName = "Balance", PlanDurationName = "1 day", StartDate = date, EndDate = date, Status = status,
                PaymentStatus = "PENDING", PlacedAt = placed, CreatedAt = placed, UpdatedAt = placed });
        }
        Booking(order.CustomerProfileId, "COMPLETED", new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero));
        Booking(beta.Id, "CONFIRMED", new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero));
        Booking(beta.Id, "ACTIVE", new(2026, 10, 6, 10, 0, 0, TimeSpan.Zero));
        Booking(beta.Id, "CONFIRMED", new(2026, 10, 27, 20, 59, 59, TimeSpan.Zero));
        Booking(boundary.Id, "CONFIRMED", new(2026, 9, 27, 21, 0, 0, TimeSpan.Zero));
        Booking(excluded.Id, "CONFIRMED", new(2026, 10, 27, 21, 0, 0, TimeSpan.Zero));
        foreach (var status in new[] { "CANCELLED", "REJECTED", "PENDING", "PENDING_APPROVAL" })
            Booking(excluded.Id, status, new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();
        var service = new OperationsDashboardService(db, clock, Options.Create(new OperationsDashboardOptions()));
        var insights = (await service.GetAsync(date, default)).CustomerInsights!;
        Assert.Equal(3, insights.CustomersWithBookings); Assert.Equal(6, insights.BookedOrders);
        Assert.Equal(2, insights.RepeatCustomers); Assert.Equal(66.7m, insights.RepeatCustomerRate);
        Assert.Equal(2, insights.NewCustomers); Assert.Equal(new DateOnly(2026, 9, 28), insights.NewCustomersFrom);
        Assert.Equal(beta.Id, insights.TopCustomers[0].CustomerId); Assert.Equal(3, insights.TopCustomers[0].BookedOrders);
        Assert.Equal("Zara repeat customer", insights.TopCustomers[0].CustomerName);
        Assert.DoesNotContain(insights.TopCustomers, c => c.CustomerId == excluded.Id);
        var empty = (await service.GetAsync(new(2020, 1, 1), default)).CustomerInsights!;
        Assert.Empty(empty.TopCustomers); Assert.Equal(0, empty.BookedOrders); Assert.Null(empty.RepeatCustomerRate);
    }
}
