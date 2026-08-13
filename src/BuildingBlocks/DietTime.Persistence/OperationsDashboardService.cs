using System.Globalization;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DietTime.Persistence;

public sealed class OperationsDashboardOptions
{
    public const string SectionName = "OperationsDashboard";
    public string BusinessTimeZone { get; set; } = "Asia/Qatar";
    public int PlansEndingSoonDays { get; set; } = 3;
    public int TodayDeliveriesLimit { get; set; } = 10;
    public string[] ActiveOrderStatuses { get; set; } = [OrderStatuses.Confirmed];
    public string[] ReviewOrderStatuses { get; set; } = [];
}

public sealed class OperationsDashboardService(
    DietTimeDbContext db,
    TimeProvider clock,
    IOptions<OperationsDashboardOptions> configuredOptions) : IOperationsDashboardService
{
    private readonly OperationsDashboardOptions options = configuredOptions.Value;

    public DateOnly GetBusinessDate()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.BusinessTimeZone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
    }

    public async Task<OperationsDashboardResponse> GetAsync(
        DateOnly date, CancellationToken cancellationToken)
    {
        var activeStatuses = NormalizeStatuses(options.ActiveOrderStatuses, [OrderStatuses.Confirmed]);
        var reviewStatuses = NormalizeStatuses(options.ReviewOrderStatuses, []);
        var endOfWindow = date.AddDays(6);

        // One compact read model supplies workload, next-delivery, and attention metrics.
        // Includes are split into a fixed three queries, avoiding a cartesian product and N+1 reads.
        var orders = await db.Orders.AsNoTracking()
            .Where(order => activeStatuses.Contains(order.Status) && order.EndDate >= date)
            .Include(order => order.DeliveryDays)
            .Include(order => order.Meals)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        var scheduledByDate = Enumerable.Range(0, 7)
            .Select(offset => date.AddDays(offset))
            .ToDictionary(day => day, day => orders.Where(order => IsScheduled(order, day)).ToArray());

        var todayOrders = scheduledByDate[date];
        var allTodayDeliveries = await BuildDeliveriesAsync(todayOrders, cancellationToken);
        var todayDeliveries = allTodayDeliveries
            .Take(Math.Max(0, options.TodayDeliveriesLimit)).ToArray();

        var nextDate = orders
            .Select(order => OperationsDashboardScheduling.NextScheduledDate(
                date.AddDays(1), order.StartDate, order.EndDate,
                order.DeliveryDays.Select(day => day.DayOfWeek).ToArray()))
            .Where(candidate => candidate.HasValue)
            .Min();
        NextDeliveryDayDto? nextDelivery = null;
        if (nextDate.HasValue)
        {
            var nextOrders = orders.Where(order => IsScheduled(order, nextDate.Value)).ToArray();
            nextDelivery = new(nextDate.Value, nextOrders.Length,
                nextOrders.Select(order => order.CustomerProfileId).Distinct().Count());
        }

        var workload = scheduledByDate.Select(pair => new DeliveryWorkloadDayDto(
            pair.Key,
            pair.Key.ToString("dddd", CultureInfo.InvariantCulture),
            pair.Value.Length,
            pair.Value.Select(order => order.CustomerProfileId).Distinct().Count(),
            pair.Value.Sum(MealCount),
            pair.Value.Length > 0)).ToArray();

        var futureOrders = orders.Where(order =>
            OperationsDashboardScheduling.NextScheduledDate(
                date, order.StartDate, order.EndDate,
                order.DeliveryDays.Select(day => day.DayOfWeek).ToArray()).HasValue).ToArray();
        var finalServiceDates = orders.Select(order => new
            {
                Order = order,
                Date = OperationsDashboardScheduling.LastScheduledDate(
                    order.StartDate, order.EndDate,
                    order.DeliveryDays.Select(day => day.DayOfWeek).ToArray())
            })
            .Where(item => item.Date.HasValue)
            .ToArray();
        var endingLimit = date.AddDays(Math.Max(0, options.PlansEndingSoonDays - 1));
        var reviewCount = reviewStatuses.Length == 0
            ? 0
            : await db.Orders.AsNoTracking().CountAsync(
                order => reviewStatuses.Contains(order.Status), cancellationToken);
        var attention = new DashboardAttentionDto(
            futureOrders.Count(order => !HasValidAddress(order)),
            reviewCount,
            finalServiceDates.Where(item => item.Date!.Value >= date && item.Date.Value <= endingLimit)
                .Select(item => item.Order.CustomerProfileId).Distinct().Count(),
            orders.Where(order => !futureOrders.Contains(order))
                .Select(order => order.CustomerProfileId).Distinct().Count(),
            0); // No execution/route model exists from which a genuine conflict can be inferred.

        var starts = orders.Where(order => order.StartDate >= date && order.StartDate <= endOfWindow)
            .GroupBy(order => order.StartDate)
            .Select(group => new PlanActivityDayDto(
                group.Key, group.Select(order => order.CustomerProfileId).Distinct().Count()))
            .OrderBy(day => day.Date).ToArray();
        var ends = finalServiceDates.Where(item => item.Date!.Value >= date && item.Date.Value <= endOfWindow)
            .GroupBy(item => item.Date!.Value)
            .Select(group => new PlanActivityDayDto(
                group.Key, group.Select(item => item.Order.CustomerProfileId).Distinct().Count()))
            .OrderBy(day => day.Date).ToArray();

        return new OperationsDashboardResponse(
            date,
            new(todayOrders.Length,
                todayOrders.Select(order => order.CustomerProfileId).Distinct().Count(),
                todayOrders.Sum(MealCount),
                null), // The schema has no reliable delivery execution/completion field.
            nextDelivery,
            workload,
            attention,
            todayDeliveries,
            new(starts, ends));
    }

    public async Task<DashboardDeliveriesPage> GetDeliveriesAsync(
        DateOnly date, int page, int pageSize, CancellationToken cancellationToken)
    {
        var activeStatuses = NormalizeStatuses(options.ActiveOrderStatuses, [OrderStatuses.Confirmed]);
        var weekday = OperationsDashboardScheduling.ToApiWeekday(date.DayOfWeek);
        var query = db.Orders.AsNoTracking().Where(order =>
            activeStatuses.Contains(order.Status) && order.StartDate <= date && order.EndDate >= date &&
            order.DeliveryDays.Any(day => day.DayOfWeek == weekday));
        var total = await query.CountAsync(cancellationToken);
        var orderedQuery =
            from order in query
            join profile in db.CustomerProfiles.AsNoTracking()
                on order.CustomerProfileId equals profile.Id
            join userProfile in db.UserProfiles.AsNoTracking()
                on profile.UserId equals (Guid?)userProfile.UserId into userProfiles
            from userProfile in userProfiles.DefaultIfEmpty()
            orderby order.DeliveryStartTime,
                userProfile != null
                    ? userProfile.FirstName + " " + userProfile.LastName
                    : profile.PreferredName,
                order.OrderNumber
            select order;
        var orders = await orderedQuery.Include(order => order.Meals)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        var items = await BuildDeliveriesAsync(orders, cancellationToken);
        return new(items, new(page, pageSize, total, (int)Math.Ceiling(total / (double)pageSize)));
    }

    private async Task<IReadOnlyList<DashboardDeliveryDto>> BuildDeliveriesAsync(
        IReadOnlyCollection<Order> orders, CancellationToken cancellationToken)
    {
        if (orders.Count == 0)
            return [];

        var profileIds = orders.Select(order => order.CustomerProfileId).Distinct().ToArray();
        var profiles = await db.CustomerProfiles.AsNoTracking()
            .Where(profile => profileIds.Contains(profile.Id))
            .Select(profile => new { profile.Id, profile.UserId, profile.PreferredName })
            .ToListAsync(cancellationToken);
        var userIds = profiles.Where(profile => profile.UserId.HasValue)
            .Select(profile => profile.UserId!.Value).Distinct().ToArray();
        var names = await db.UserProfiles.AsNoTracking()
            .Where(profile => userIds.Contains(profile.UserId))
            .ToDictionaryAsync(profile => profile.UserId,
                profile => (profile.FirstName + " " + profile.LastName).Trim(), cancellationToken);
        var resolvedNames = profiles.ToDictionary(profile => profile.Id, profile =>
            profile.UserId.HasValue && names.TryGetValue(profile.UserId.Value, out var name) && name.Length > 0
                ? name
                : !string.IsNullOrWhiteSpace(profile.PreferredName)
                    ? profile.PreferredName.Trim()
                    : $"Customer {profile.Id:N}"[..17]);

        return orders.Select(order => new DashboardDeliveryDto(
                order.Id,
                order.OrderNumber,
                order.CustomerProfileId,
                resolvedNames.GetValueOrDefault(order.CustomerProfileId, $"Customer {order.CustomerProfileId:N}"[..17]),
                order.MealPlanTemplateId,
                order.PlanName,
                MealCount(order),
                order.DeliveryArea,
                FormatAddress(order),
                order.Status))
            .OrderBy(delivery => orders.First(order => order.Id == delivery.OrderId).DeliveryStartTime)
            .ThenBy(delivery => delivery.CustomerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(delivery => delivery.OrderNumber, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsScheduled(Order order, DateOnly date) =>
        OperationsDashboardScheduling.IsScheduled(date, order.StartDate, order.EndDate,
            order.DeliveryDays.Select(day => day.DayOfWeek).ToArray());

    private static int MealCount(Order order) => order.Meals.Sum(meal => meal.Quantity);

    private static bool HasValidAddress(Order order) =>
        CustomerAddressTypes.All.Contains(order.DeliveryAddressType) &&
        !string.IsNullOrWhiteSpace(order.DeliveryArea) &&
        (!order.DeliveryLatitude.HasValue || order.DeliveryLatitude is >= -90 and <= 90) &&
        (!order.DeliveryLongitude.HasValue || order.DeliveryLongitude is >= -180 and <= 180);

    private static string FormatAddress(Order order)
    {
        if (!string.IsNullOrWhiteSpace(order.DeliveryFormattedAddress))
            return order.DeliveryFormattedAddress.Trim();
        return string.Join(", ", new[]
        {
            order.DeliveryUnitNumber, order.DeliveryBuildingNo, order.DeliveryStreetNo,
            order.DeliveryZoneNo, order.DeliveryArea
        }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()));
    }

    private static string[] NormalizeStatuses(IEnumerable<string>? values, string[] fallback)
    {
        var statuses = values?.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToUpperInvariant()).Distinct().ToArray() ?? [];
        return statuses.Length == 0 ? fallback : statuses;
    }
}
