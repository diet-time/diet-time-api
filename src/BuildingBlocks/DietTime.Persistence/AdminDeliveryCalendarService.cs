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

    public async Task<DeliveryPreparationSummaryResponse> GetPreparationSummaryAsync(
        DateOnly date,
        CancellationToken cancellationToken)
    {
        var apiWeekday = OperationsDashboardScheduling.ToApiWeekday(date.DayOfWeek);
        var menuWeekday = MenuWeekdayExtensions.FromDate(date);
        var scheduledOrders = await db.Orders.AsNoTracking()
            .Where(order => (order.Status == OrderStatuses.Confirmed || order.Status == "ACTIVE") &&
                order.StartDate <= date && order.EndDate >= date &&
                order.DeliveryDays.Any(day => day.DayOfWeek == apiWeekday))
            .Select(order => new DeliveryPreparationOrderSource(
                order.Id,
                order.CustomerProfileId,
                order.MealPlanTemplateId,
                order.PlanName))
            .ToListAsync(cancellationToken);

        if (scheduledOrders.Count == 0)
            return DeliveryPreparationAggregation.Build(date, [], []);

        var now = clock.GetUtcNow();
        // Finalization and customer edits acquire the same row locks. Already
        // finalized days are unchanged, so report downloads are idempotent.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE public.subscription_service_days SET selection_locked = TRUE, selection_status = 'LOCKED', revision = revision + 1, updated_at = {now} WHERE order_id IS NOT NULL AND service_date = {date} AND selection_cutoff_at <= {now} AND NOT selection_locked", cancellationToken);
        var trackedDays = await db.Set<SubscriptionServiceDay>().AsNoTracking()
            .Where(day => day.ServiceDate == date && day.OrderId != null)
            .Select(day => new { day.OrderId, day.SelectionLocked, day.ReviewReason })
            .ToListAsync(cancellationToken);
        var trackedOrderIds = trackedDays.Select(day => day.OrderId!.Value).ToHashSet();
        var scheduledIds = scheduledOrders.Select(order => order.OrderId).ToArray();
        var persistedRows = await (
            from selection in db.Set<CustomerMealSelection>().AsNoTracking()
            join day in db.Set<SubscriptionServiceDay>().AsNoTracking() on selection.SubscriptionServiceDayId equals day.Id
            join meal in db.MealItems.AsNoTracking() on selection.MenuItemId equals meal.Id
            join type in db.MealTypes.AsNoTracking() on selection.MealTypeId equals type.Id
            where day.ServiceDate == date && day.OrderId != null && scheduledIds.Contains(day.OrderId.Value) &&
                day.ReviewReason == null && selection.Status != "CANCELLED"
            select new DeliveryPreparationMenuSource(day.OrderId!.Value, type.Id,
                selection.Category == "snack" ? "Snack" : "Meal", type.DisplayOrder, meal.Id,
                meal.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault() ?? meal.Sku, 1))
            .ToListAsync(cancellationToken);
        DeliveryPreparationSummaryResponse Build(IReadOnlyCollection<DeliveryPreparationMenuSource> legacyRows)
        {
            var summary = DeliveryPreparationAggregation.Build(date, scheduledOrders,
                legacyRows.Concat(persistedRows).ToArray());
            return summary with { Status = trackedDays.Any(d => d.ReviewReason != null) ? "NeedsReview"
                : trackedDays.Any(d => !d.SelectionLocked) ? "Provisional" : summary.Status };
        }
        // Legacy orders keep their historical reporting path; new service days
        // always use the customer's persisted choices, never regenerated defaults.
        var orderIds = scheduledOrders.Where(order => !trackedOrderIds.Contains(order.OrderId))
            .Select(order => order.OrderId).ToArray();
        var templateIds = scheduledOrders.Select(order => order.MealPlanId).Distinct().ToArray();
        var templates = await db.MealPlanTemplates.AsNoTracking()
            .Where(template => templateIds.Contains(template.Id))
            .Select(template => new { template.Id, template.CycleWeeks, template.ValidFrom })
            .ToListAsync(cancellationToken);
        var templateById = templates.ToDictionary(template => template.Id);
        var templateDays = await db.MealPlanTemplateDays.AsNoTracking()
            .Where(day => templateIds.Contains(day.MealPlanTemplateId) && day.IsActive)
            .Select(day => new { day.Id, day.MealPlanTemplateId, day.WeekNumber, day.MenuWeekday })
            .ToListAsync(cancellationToken);
        var menuDayIds = templateDays
            .Where(day =>
            {
                if (!templateById.TryGetValue(day.MealPlanTemplateId, out var template))
                    return false;
                var week = MenuRotationResolver.ResolveWeek(
                    date,
                    template.CycleWeeks,
                    template.ValidFrom,
                    rotationOptions.Value.AnchorDate);
                return week.HasValue && day.WeekNumber == week.Value && day.MenuWeekday == menuWeekday;
            })
            .Select(day => day.Id)
            .ToArray();
        if (menuDayIds.Length == 0)
            return Build([]);

        var menuCandidates = await (
            from orderMeal in db.OrderMeals.AsNoTracking()
            where orderIds.Contains(orderMeal.OrderId)
            join order in db.Orders.AsNoTracking()
                on orderMeal.OrderId equals order.Id
            join day in db.MealPlanTemplateDays.AsNoTracking()
                on order.MealPlanTemplateId equals day.MealPlanTemplateId
            join slot in db.MealPlanTemplateSlots.AsNoTracking()
                on new { DayId = day.Id, orderMeal.MealTypeId }
                equals new { DayId = slot.MealPlanTemplateDayId, slot.MealTypeId }
            join option in db.MealPlanSlotOptions.AsNoTracking()
                on slot.Id equals option.MealPlanTemplateSlotId
            join mealType in db.MealTypes.AsNoTracking()
                on orderMeal.MealTypeId equals mealType.Id
            join menuItem in db.MealItems.AsNoTracking()
                on option.MealItemId equals menuItem.Id
            where menuDayIds.Contains(day.Id) && day.MenuWeekday == menuWeekday
            select new
            {
                orderMeal.OrderId,
                orderMeal.MealTypeId,
                orderMeal.MealTypeName,
                MealTypeDisplayOrder = mealType.DisplayOrder,
                MenuItemId = menuItem.Id,
                MenuItemName = menuItem.Translations
                    .Where(translation => translation.LanguageCode == "en")
                    .Select(translation => translation.Name)
                    .FirstOrDefault() ?? menuItem.Sku,
                orderMeal.Quantity,
                option.IsDefault,
                option.IsAvailable,
                option.DisplayOrder
            })
            .ToListAsync(cancellationToken);

        // Orders retain their plan version, so preparation must still work when an
        // option or slot is later made unavailable. Prefer the configured default;
        // legacy plans without one fall back deterministically to their first option.
        var menuRows = menuCandidates
            .GroupBy(row => new { row.OrderId, row.MealTypeId })
            .Select(group => group
                .OrderByDescending(row => row.IsDefault)
                .ThenByDescending(row => row.IsAvailable)
                .ThenBy(row => row.DisplayOrder)
                .ThenBy(row => row.MenuItemName, StringComparer.OrdinalIgnoreCase)
                .First())
            .Select(row => new DeliveryPreparationMenuSource(
                row.OrderId,
                row.MealTypeId,
                row.MealTypeName,
                row.MealTypeDisplayOrder,
                row.MenuItemId,
                row.MenuItemName,
                row.Quantity))
            .ToArray();

        return Build(menuRows);
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
