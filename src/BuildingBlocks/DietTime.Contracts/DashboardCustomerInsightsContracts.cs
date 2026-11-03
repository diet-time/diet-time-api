namespace DietTime.Contracts;

public sealed record DashboardCustomerInsightsDto(
    DateOnly ThroughDate, DateOnly NewCustomersFrom, int CustomersWithBookings, int RepeatCustomers,
    int NewCustomers, int BookedOrders, IReadOnlyList<DashboardTopCustomerDto> TopCustomers)
{
    public decimal? RepeatCustomerRate => CustomersWithBookings > 0
        ? decimal.Round(100m * RepeatCustomers / CustomersWithBookings, 1) : null;
}

public sealed record DashboardTopCustomerDto(Guid CustomerId, string CustomerName, int BookedOrders, DateTimeOffset LatestBookingAt);
