using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Persistence;

public sealed class AdminDeliveryCalendarService(DietTimeDbContext db) : IAdminDeliveryCalendarService
{
    public async Task<AdminDeliveryCalendarResponse> GetMonthAsync(
        DateOnly startDate,
        DateOnly endDate,
        Guid? planId,
        string? orderStatus,
        CancellationToken cancellationToken)
    {
        var query = db.Orders.AsNoTracking()
            .Where(order => order.StartDate <= endDate && order.EndDate >= startDate);

        if (planId.HasValue)
            query = query.Where(order => order.MealPlanTemplateId == planId.Value);
        if (!string.IsNullOrWhiteSpace(orderStatus))
        {
            var normalizedStatus = orderStatus.Trim().ToUpperInvariant();
            query = query.Where(order => order.Status == normalizedStatus);
        }

        var orders = await query
            .Include(order => order.DeliveryDays)
            .Include(order => order.Meals)
            .OrderBy(order => order.DeliveryStartTime)
            .ThenBy(order => order.OrderNumber)
            .ToListAsync(cancellationToken);

        var profileIds = orders.Select(order => order.CustomerProfileId).Distinct().ToArray();
        var profiles = await db.CustomerProfiles.AsNoTracking()
            .Where(profile => profileIds.Contains(profile.Id))
            .Select(profile => new { profile.Id, profile.UserId, profile.PreferredName })
            .ToListAsync(cancellationToken);
        var userIds = profiles.Where(profile => profile.UserId.HasValue)
            .Select(profile => profile.UserId!.Value).Distinct().ToArray();
        var userNames = await db.UserProfiles.AsNoTracking()
            .Where(profile => userIds.Contains(profile.UserId))
            .Select(profile => new { profile.UserId, profile.FirstName, profile.LastName })
            .ToDictionaryAsync(
                profile => profile.UserId,
                profile => $"{profile.FirstName} {profile.LastName}".Trim(),
                cancellationToken);
        var userEmails = await db.Users.AsNoTracking()
            .Where(user => userIds.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, user => user.Email, cancellationToken);
        var customerNames = profiles.ToDictionary(
            profile => profile.Id,
            profile => ResolveCustomerName(profile.UserId, profile.PreferredName, userNames, userEmails, profile.Id));

        var days = new List<AdminDeliveryCalendarDayResponse>();
        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            var scheduledOrders = orders.Where(order =>
                    AdminDeliveryCalendarScheduling.IsScheduled(
                        date, order.StartDate, order.EndDate,
                        order.DeliveryDays.Select(day => day.DayOfWeek).ToArray()))
                .ToArray();
            var orderResponses = scheduledOrders.Select(order => new AdminDeliveryCalendarOrderResponse(
                order.Id,
                order.OrderNumber,
                order.CustomerProfileId,
                customerNames.GetValueOrDefault(order.CustomerProfileId, $"Customer {order.CustomerProfileId:N}"[..17]),
                order.MealPlanTemplateId,
                order.PlanName,
                order.Meals.Sum(meal => meal.Quantity),
                order.DeliveryTimeSlotName,
                order.Status)).ToArray();
            var mealTypeTotals = scheduledOrders.SelectMany(order => order.Meals)
                .GroupBy(meal => meal.MealTypeName)
                .Select(group => new AdminDeliveryMealTypeTotalResponse(group.Key, group.Sum(meal => meal.Quantity)))
                .OrderBy(group => group.MealType)
                .ToArray();

            days.Add(new AdminDeliveryCalendarDayResponse(
                date,
                orderResponses.Length,
                scheduledOrders.Select(order => order.CustomerProfileId).Distinct().Count(),
                mealTypeTotals.Sum(total => total.Quantity),
                orderResponses,
                mealTypeTotals));
        }

        return new AdminDeliveryCalendarResponse(startDate, endDate, days);
    }

    private static string ResolveCustomerName(
        Guid? userId,
        string? preferredName,
        IReadOnlyDictionary<Guid, string> names,
        IReadOnlyDictionary<Guid, string?> emails,
        Guid profileId) =>
        userId.HasValue && names.TryGetValue(userId.Value, out var fullName) && !string.IsNullOrWhiteSpace(fullName)
            ? fullName
            : !string.IsNullOrWhiteSpace(preferredName)
                ? preferredName.Trim()
                : userId.HasValue && emails.TryGetValue(userId.Value, out var email) && !string.IsNullOrWhiteSpace(email)
                    ? email
                    : $"Customer {profileId:N}"[..17];

}
