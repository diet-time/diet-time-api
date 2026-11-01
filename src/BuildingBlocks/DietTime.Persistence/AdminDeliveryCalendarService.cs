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
            .Where(OrderWorkflowPolicy.FulfillmentFilter)
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
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        var orderIds = orders.Select(o => o.Id).ToArray();
        var serviceDays = await db.Set<SubscriptionServiceDay>().AsNoTracking()
            .Where(d => d.OrderId != null && orderIds.Contains(d.OrderId.Value) && d.ServiceDate >= startDate && d.ServiceDate <= endDate)
            .ToListAsync(cancellationToken);
        var orderLookup = orders.ToDictionary(o => o.Id);
        var eligibleDays = serviceDays.Where(d => DailyFulfillmentTracking.IsEligible(d, orderLookup[d.OrderId!.Value]))
            .DistinctBy(d => d.Id).ToLookup(d => d.ServiceDate);
        var slotIds = orders.Select(o => o.DeliveryTimeSlotId).Distinct().ToArray();
        var slots = await db.DeliveryTimeSlots.AsNoTracking().Where(s => slotIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, cancellationToken);
        var profileIds = orders.Select(o => o.CustomerProfileId).Distinct().ToArray();
        var profiles = await db.CustomerProfiles.AsNoTracking().Where(p => profileIds.Contains(p.Id))
            .Select(p => new { p.Id, p.UserId, p.PreferredName }).ToListAsync(cancellationToken);
        var userIds = profiles.Where(p => p.UserId.HasValue).Select(p => p.UserId!.Value)
            .Concat(serviceDays.Where(d => d.FulfilledBy.HasValue).Select(d => d.FulfilledBy!.Value)).Distinct().ToArray();
        var names = await db.UserProfiles.AsNoTracking().Where(p => userIds.Contains(p.UserId))
            .Select(p => new { p.UserId, p.FirstName, p.LastName }).ToDictionaryAsync(p => p.UserId, p => (p.FirstName + " " + p.LastName).Trim(), cancellationToken);
        var emails = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Email, cancellationToken);
        var customerNames = profiles.ToDictionary(p => p.Id, p => ResolveCustomerName(p.UserId, p.PreferredName, names, emails, p.Id));
        var now = clock.GetUtcNow();
        var days = new List<AdminDeliveryCalendarDayResponse>();
        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            var scheduled = eligibleDays[date].ToArray();
            var scheduledOrders = scheduled.Select(d => orderLookup[d.OrderId!.Value]).DistinctBy(o => o.Id).ToArray();
            var orderResponses = scheduled.Select(day => {
                var order = orderLookup[day.OrderId!.Value];
                var deadline = DailyFulfillmentTracking.Deadline(day.ServiceDate, order, order.DeliveryTimeSlotId is { } slotId ? slots.GetValueOrDefault(slotId) : null);
                return new AdminDeliveryCalendarOrderResponse(order.Id, order.OrderNumber, order.CustomerProfileId,
                    customerNames.GetValueOrDefault(order.CustomerProfileId, "Name not recorded"), order.MealPlanTemplateId, order.PlanName,
                    order.Meals.Sum(m => m.Quantity), order.DeliveryTimeSlotName, order.Status, order.FulfillmentMethod, order.TakeawayBranchName, order.TakeawayBranchAddress)
                {
                    ServiceDayId = day.Id, ServiceDate = day.ServiceDate,
                    FulfillmentStatus = ServiceDayFulfillment.DisplayStatus(day.Status),
                    FulfilledAt = day.FulfilledAt, FulfilledBy = day.FulfilledBy,
                    StaffDisplayName = day.FulfilledBy is { } staff ? names.GetValueOrDefault(staff) : null,
                    DeliveryAddress = SavedDeliveryAddress(order), FulfillmentDeadline = deadline,
                    IsOverdue = DailyFulfillmentTracking.IsOverdue(day.ServiceDate, day.Status, deadline, now)
                };
            }).OrderBy(r => r.IsOverdue ? 0 : r.FulfillmentStatus == "SCHEDULED" ? 1 : 2).ToArray();
            var progress = DailyFulfillmentTracking.Summarize(orderResponses.Select(r => new ServiceDayProgress(r.FulfillmentStatus!, r.IsOverdue)));
            var mealTypeTotals = scheduledOrders.SelectMany(o => o.Meals).GroupBy(m => m.MealTypeName)
                .Select(g => new AdminDeliveryMealTypeTotalResponse(g.Key, g.Sum(m => m.Quantity))).OrderBy(g => g.MealType).ToArray();
            days.Add(new AdminDeliveryCalendarDayResponse(date, scheduledOrders.Length,
                scheduledOrders.Select(o => o.CustomerProfileId).Distinct().Count(), mealTypeTotals.Sum(t => t.Quantity), orderResponses, mealTypeTotals)
            {
                TotalServiceDays = progress.TotalServiceDays, PendingCount = progress.PendingCount, OverdueCount = progress.OverdueCount,
                DeliveredCount = progress.DeliveredCount, CollectedCount = progress.CollectedCount, FulfillmentProgress = progress.Status
            });
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
            .Where(OrderWorkflowPolicy.FulfillmentFilter)
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
            select new { Day = day, Order = order }).ToListAsync(cancellationToken);
        var profileIds = details.Select(d => d.Order.CustomerProfileId).Distinct().ToArray();
        var profiles = await db.CustomerProfiles.AsNoTracking().Include(p => p.Preferences).Include(p => p.Allergens)
            .ThenInclude(a => a.Allergen).ThenInclude(a => a.Translations)
            .Where(p => profileIds.Contains(p.Id)).AsSplitQuery().ToDictionaryAsync(p => p.Id, cancellationToken);
        var userIds = profiles.Values.Where(p => p.UserId.HasValue).Select(p => p.UserId!.Value).Distinct().ToArray();
        var userNames = await db.UserProfiles.AsNoTracking().Where(p => userIds.Contains(p.UserId))
            .Select(p => new { p.UserId, p.FirstName, p.LastName })
            .ToDictionaryAsync(p => p.UserId,
                p => $"{p.FirstName} {p.LastName}".Trim(), cancellationToken);
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
            TotalScheduledCount = details.Count(d => DailyFulfillmentTracking.IsTrackableStatus(d.Day.Status)),
            DeliveredCount = details.Count(d => d.Day.Status == "DELIVERED"),
            CollectedCount = details.Count(d => d.Day.Status == "COLLECTED"),
            RemainingCount = details.Count(d => DailyFulfillmentTracking.IsTrackableStatus(d.Day.Status) && d.Day.Status is not ("DELIVERED" or "COLLECTED")),
            Status = exceptions.Length > 0 ? "NeedsReview" : preliminary ? "Preliminary" : "Finalized",
            IsPreliminary = preliminary,
            OperationalExceptions = exceptions,
            Selections = details.Select(d => {
                profiles.TryGetValue(d.Order.CustomerProfileId, out var profile);
                var rows = selections.Where(s => s.Id == d.Day.Id).ToArray();
                var name = profile?.UserId is { } userId ? userNames.GetValueOrDefault(userId) : null;
                if (string.IsNullOrWhiteSpace(name)) name = profile?.PreferredName?.Trim();
                return new KitchenSelectionResponse(d.Day.Id, d.Order.OrderNumber, d.Order.CustomerProfileId,
                    d.Day.CustomerConfirmedAt != null ? "CUSTOMER_CONFIRMED" : d.Day.AutoFinalizedAt != null ? "AUTO_FINALIZED" : "UNFINALIZED",
                    rows.Select(r => r.SelectionSource).Distinct().ToArray(),
                    rows.GroupBy(r => new { r.Name, r.Category }).Select(g => new KitchenDishResponse(g.Key.Name, g.Key.Category ?? "meal", g.Count())).ToArray(),
                    profile?.Preferences.Select(p => p.PreferenceCode).Concat(profile.Allergens.Select(a => "ALLERGEN: " + a.Allergen.Code)).ToArray() ?? [], d.Day.ReviewReason,
                    d.Order.FulfillmentMethod, d.Order.TakeawayBranchName, d.Order.TakeawayBranchAddress)
                {
                    FulfillmentStatus = ServiceDayFulfillment.DisplayStatus(d.Day.Status),
                    FulfilledAt = d.Day.FulfilledAt,
                    FulfilledBy = d.Day.FulfilledBy,
                    CustomerName = string.IsNullOrWhiteSpace(name) ? "Name not recorded" : name,
                    DeliveryAddress = SavedDeliveryAddress(d.Order),
                    DietaryRestrictionLabels = profile?.Preferences.Where(p => p.PreferenceCode != "NONE")
                        .Select(p => PreparationDisplayText.Label(p.PreferenceCode))
                        .Concat(profile.Allergens.Select(a => "Allergen: " +
                            (a.Allergen.Translations.FirstOrDefault(t => t.LanguageCode == "en" && !string.IsNullOrWhiteSpace(t.Name))?.Name
                             ?? PreparationDisplayText.Label(a.Allergen.Code))))
                        .Distinct().ToArray() ?? []
                };
            }).ToArray()
        };
    }

    private static string? SavedDeliveryAddress(Order order)
    {
        if (order.FulfillmentMethod == FulfillmentMethods.Takeaway) return null;
        var address = !string.IsNullOrWhiteSpace(order.DeliveryFormattedAddress)
            ? order.DeliveryFormattedAddress.Trim()
            : string.Join(", ", new[] {
                order.DeliveryAddressName,
                string.IsNullOrWhiteSpace(order.DeliveryBuildingNo) ? null : "Building " + order.DeliveryBuildingNo,
                string.IsNullOrWhiteSpace(order.DeliveryStreetNo) ? null : "Street " + order.DeliveryStreetNo,
                string.IsNullOrWhiteSpace(order.DeliveryUnitNumber) ? null : "Unit " + order.DeliveryUnitNumber,
                string.IsNullOrWhiteSpace(order.DeliveryZoneNo) ? null : "Zone " + order.DeliveryZoneNo,
                order.DeliveryArea
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (!string.IsNullOrWhiteSpace(order.DeliveryDirections))
            address = string.Join(" · ", new[] { address, order.DeliveryDirections }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(address) ? null : address;
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
