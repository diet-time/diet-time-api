using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DietTime.Persistence;

public sealed class AdminDeliveryCalendarService(
    DietTimeDbContext db,
    IOptions<MenuRotationOptions> rotationOptions,
    TimeProvider clock) : IAdminDeliveryCalendarService
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
                order.Status, order.FulfillmentMethod)).ToArray();
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

    public async Task<DeliveryPreparationSummaryResponse> GetPreparationSummaryAsync(
        DateOnly date,
        CancellationToken cancellationToken)
    {
        _ = rotationOptions; // Legacy constructor compatibility; final reports use persisted selections.
        if (date.DayOfWeek == DayOfWeek.Friday) return DeliveryPreparationAggregation.Build(date, [], []);
        var apiWeekday = OperationsDashboardScheduling.ToApiWeekday(date.DayOfWeek);
        var scheduledOrders = await db.Orders.AsNoTracking()
            .Where(order => (order.Status == OrderStatuses.Confirmed || order.Status == "ACTIVE") &&
                order.StartDate <= date && order.EndDate >= date &&
                order.DeliveryDays.Any(day => day.DayOfWeek == apiWeekday) &&
                !db.Set<SubscriptionServiceDay>().Any(day => day.OrderId == order.Id && day.ServiceDate == date &&
                    (day.Status == "SKIPPED" || day.Status == "CANCELLED")))
            .Select(order => new DeliveryPreparationOrderSource(
                order.Id,
                order.CustomerProfileId,
                order.MealPlanTemplateId,
                order.PlanName))
            .ToListAsync(cancellationToken);

        if (scheduledOrders.Count == 0)
            return DeliveryPreparationAggregation.Build(date, [], []);

        var now = clock.GetUtcNow();
        var scheduledIds = scheduledOrders.Select(order => order.OrderId).ToArray();
        var trackedDays = await db.Set<SubscriptionServiceDay>().AsNoTracking()
            .Where(day => day.ServiceDate == date && day.OrderId != null && scheduledIds.Contains(day.OrderId.Value))
            .Select(day => new { day.OrderId, day.SelectionLocked, day.SelectionStatus, day.ReviewReason, day.CustomerConfirmedAt, day.AutoFinalizedAt })
            .ToListAsync(cancellationToken);
        var trackedOrderIds = trackedDays.Select(day => day.OrderId!.Value).ToHashSet();
        var persistedRows = await (
            from selection in db.Set<CustomerMealSelection>().AsNoTracking()
            join day in db.Set<SubscriptionServiceDay>().AsNoTracking() on selection.SubscriptionServiceDayId equals day.Id
            join meal in db.MealItems.AsNoTracking() on selection.MenuItemId equals meal.Id
            join type in db.MealTypes.AsNoTracking() on selection.MealTypeId equals type.Id
            where day.ServiceDate == date && day.OrderId != null && scheduledIds.Contains(day.OrderId.Value) &&
                day.ReviewReason == null && day.SelectionLocked && (day.CustomerConfirmedAt != null || day.AutoFinalizedAt != null) && selection.Status != "CANCELLED"
            select new DeliveryPreparationMenuSource(day.OrderId!.Value, type.Id,
                selection.Category == "snack" ? "Snack" : "Meal", type.DisplayOrder, meal.Id,
                meal.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault() ?? meal.Sku, 1))
            .ToListAsync(cancellationToken);
        var details = await (
            from day in db.Set<SubscriptionServiceDay>().AsNoTracking()
            join order in db.Orders.AsNoTracking() on day.OrderId equals (Guid?)order.Id
            where day.ServiceDate == date && scheduledIds.Contains(order.Id)
            select new { Day = day, order.OrderNumber, order.CustomerProfileId }).ToListAsync(cancellationToken);
        var profileIds = details.Select(d => d.CustomerProfileId).Distinct().ToArray();
        var profiles = await db.CustomerProfiles.AsNoTracking().Include(p => p.Preferences).Include(p => p.Allergens)
            .ThenInclude(a => a.Allergen).Where(p => profileIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        var selections = await (from selection in db.Set<CustomerMealSelection>().AsNoTracking()
            join day in db.Set<SubscriptionServiceDay>() on selection.SubscriptionServiceDayId equals day.Id
            join meal in db.MealItems on selection.MenuItemId equals meal.Id
            where day.ServiceDate == date && day.OrderId != null && scheduledIds.Contains(day.OrderId.Value) &&
                day.SelectionLocked && (day.CustomerConfirmedAt != null || day.AutoFinalizedAt != null) && day.ReviewReason == null && selection.Status != "CANCELLED"
            select new { day.Id, selection.SelectionSource, selection.Category, Name = meal.Translations
                .Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault() ?? meal.Sku }).ToListAsync(cancellationToken);
        var exceptions = trackedDays.Where(d => d.ReviewReason != null).Select(d => $"Order {d.OrderId}: {d.ReviewReason}")
            .Concat(trackedDays.Where(d => (d.SelectionLocked || d.SelectionStatus == "LOCKED") && d.CustomerConfirmedAt == null && d.AutoFinalizedAt == null)
                .Select(d => $"Order {d.OrderId}: legacy lock has no validated finalization timestamp; review required."))
            .Concat(scheduledOrders.Where(o => !trackedOrderIds.Contains(o.OrderId)).Select(o => $"Order {o.OrderId}: service days have not been initialized; review required."))
            .ToArray();
        var preliminary = now < SubscriptionMenuRules.TomorrowCutoff(date) || trackedDays.Any(d => !d.SelectionLocked) || exceptions.Length > 0;
        var summary = DeliveryPreparationAggregation.Build(date, scheduledOrders, persistedRows);
        return summary with {
            Status = exceptions.Length > 0 ? "NeedsReview" : preliminary ? "Preliminary" : "Finalized",
            IsPreliminary = preliminary,
            OperationalExceptions = exceptions,
            Selections = details.Select(d => {
                profiles.TryGetValue(d.CustomerProfileId, out var profile);
                var rows = selections.Where(s => s.Id == d.Day.Id).ToArray();
                return new KitchenSelectionResponse(d.Day.Id, d.OrderNumber, d.CustomerProfileId,
                    d.Day.CustomerConfirmedAt != null ? "CUSTOMER_CONFIRMED" : d.Day.AutoFinalizedAt != null ? "AUTO_FINALIZED" : "UNFINALIZED",
                    rows.Select(r => r.SelectionSource).Distinct().ToArray(),
                    rows.GroupBy(r => new { r.Name, r.Category }).Select(g => new KitchenDishResponse(g.Key.Name, g.Key.Category ?? "meal", g.Count())).ToArray(),
                    profile?.Preferences.Select(p => p.PreferenceCode).Concat(profile.Allergens.Select(a => "ALLERGEN: " + a.Allergen.Code)).ToArray() ?? [], d.Day.ReviewReason);
            }).ToArray()
        };
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
