using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DietTime.Persistence;

public sealed class OrderReviewException(int status, string code, string detail) : Exception(detail)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public sealed class StaffOrderService(DietTimeDbContext db, TimeProvider clock, IOptions<OrderOptions> options,
    IUpcomingMealService meals, IOrderNotificationQueue notifications)
{
    public async Task<StaffOrderListResponse> ListAsync(string? status, int page, int size, CancellationToken ct, DateOnly? serviceDate = null)
    {
        var query = db.Orders.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        if (serviceDate is { } day)
        {
            var weekday = day.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)day.DayOfWeek;
            // Purchased weekdays exist before approval; fulfillment records do not.
            query = query.Where(x => x.StartDate <= day && x.EndDate >= day &&
                x.DeliveryDays.Any(d => d.DayOfWeek == weekday));
        }
        var total = await query.CountAsync(ct);
        var orders = await query.OrderByDescending(x => x.PlacedAt).ThenBy(x => x.Id)
            .Skip((page-1)*size).Take(size).Include(x => x.Meals).Include(x => x.DeliveryDays)
            .Include(x => x.StatusHistory).AsSplitQuery().ToArrayAsync(ct);
        return new(await MapAsync(orders, ct), total, page, size);
    }

    public async Task<StaffOrderResponse?> GetAsync(Guid id, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().Include(x => x.Meals).Include(x => x.DeliveryDays)
            .Include(x => x.StatusHistory).AsSplitQuery().SingleOrDefaultAsync(x => x.Id == id, ct);
        return order is null ? null : (await MapAsync([order], ct))[0];
    }

    private async Task<StaffOrderResponse[]> MapAsync(Order[] orders, CancellationToken ct)
    {
        var ids = orders.SelectMany(o => o.Meals).Select(m => m.MealTypeId).Distinct().ToArray();
        var codes = await db.MealTypes.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Code, ct);
        var profileIds = orders.Select(o => o.CustomerProfileId).ToArray();
        var customers = await (from p in db.CustomerProfiles.AsNoTracking() join u in db.Users.AsNoTracking() on p.UserId equals u.Id
            where profileIds.Contains(p.Id) select new { p.Id, Name = p.PreferredName ?? u.UserName ?? "Customer", u.PhoneNumber }).ToDictionaryAsync(x => x.Id, ct);
        return orders.Select(o => new StaffOrderResponse(OrderService.Map(o, codes), customers.GetValueOrDefault(o.CustomerProfileId)?.Name ?? "Customer",
            customers.GetValueOrDefault(o.CustomerProfileId)?.PhoneNumber, o.StatusHistory.OrderBy(x => x.ChangedAt)
                .Select(x => new OrderHistoryResponse(x.Id, x.PreviousStatus, x.Status, x.ChangedBy, x.ChangedAt, x.Notes, x.PreviousPaymentStatus, x.PaymentStatus)).ToArray())).ToArray();
    }

    public async Task<StaffOrderResponse> ChangeAsync(Guid id, string action, long version, string? reason, Guid actor,
        CancellationToken ct, RecordOrderPaymentRequest? payment = null, ReviseOrderDatesRequest? dates = null, Guid? customerUserId = null)
    {
        if (version < 1 || reason?.Length > 500) throw Error(400, "invalid_review", "A current row version and a reason of at most 500 characters are required.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM public.orders WHERE id = {id} FOR UPDATE", ct);
        var order = await db.Orders.Include(x => x.Meals).Include(x => x.DeliveryDays).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (order is null || customerUserId.HasValue && !await db.CustomerProfiles.AnyAsync(p => p.Id == order.CustomerProfileId && p.UserId == customerUserId, ct))
            throw Error(404, "order_not_found", "Order was not found.");
        if (order.RowVersion != version) throw Error(409, "order_changed", "This order changed. Refresh it before taking another action.");
        reason = reason?.Trim();
        if (action is "reject" or "hold" or "cancel" or "payment" or "revise-dates" && string.IsNullOrWhiteSpace(reason))
            throw Error(400, "reason_required", "Enter a reason for this action.");
        var now = clock.GetUtcNow();
        var today = SubscriptionMenuRules.QatarToday(now);
        var previous = order.Status;
        var previousPayment = order.PaymentStatus;
        string? eventCode = null;
        if (action == "payment")
        {
            var next = payment!.PaymentStatus;
            if (!OrderWorkflowPolicy.CanChangePayment(order.PaymentStatus, next)) throw Error(409, "invalid_payment_transition", "This payment transition is not allowed.");
            order.PaymentStatus = next;
            if (next == PaymentStatuses.Paid && order.Status is "REJECTED" or "CANCELLED") order.PaymentStatus = PaymentStatuses.RefundPending;
            else if (next == PaymentStatuses.Paid)
            {
                eventCode = "PAYMENT_RECEIVED";
                if (order.RequiresPrepayment && order.StartDate < today && !await db.Set<SubscriptionServiceDay>().AnyAsync(d => d.OrderId == id, ct))
                    order.FulfillmentBlockedReason = "The start date passed before payment. Please agree revised service dates with Diet Time.";
            }
        }
        else if (action == "revise-dates")
        {
            if (order.Status is not ("PENDING_APPROVAL" or "PENDING" or "ON_HOLD") && !(order.Status == "CONFIRMED" && order.FulfillmentBlockedReason != null)) throw Error(409, "invalid_transition", "Only orders under review or blocked before activation can have explicitly revised dates.");
            if (await db.Set<SubscriptionServiceDay>().AnyAsync(d => d.OrderId == id, ct)) throw Error(409, "schedule_review_required", "This order already has service records. Operations must review them before revising dates.");
            if (dates!.ServiceWeekdays.Count == 0 || dates.ServiceWeekdays.Any(d => d is < 1 or > 7 or 5) || dates.ServiceWeekdays.Distinct().Count() != dates.ServiceWeekdays.Count)
                throw Error(400, "invalid_service_days", "Select distinct service weekdays excluding Friday.");
            // Purchased duration is the original service-day count, not a newly edited price duration.
            var duration = SubscriptionMenuRules.ServiceDates(order.StartDate, order.EndDate, order.DeliveryDays.Select(d => d.DayOfWeek).ToHashSet()).Count();
            if (duration < 1) throw Error(409, "schedule_review_required", "The purchased service duration needs operational review.");
            order.StartDate = dates.StartDate;
            order.EndDate = OrderSchedulingRules.CalculateEndDate(dates.StartDate, dates.ServiceWeekdays, duration);
            foreach (var oldDay in order.DeliveryDays.Where(d => !dates.ServiceWeekdays.Contains(d.DayOfWeek)).ToArray())
            { db.OrderDeliveryDays.Remove(oldDay); order.DeliveryDays.Remove(oldDay); }
            foreach (var weekday in dates.ServiceWeekdays.Where(d => !order.DeliveryDays.Any(old => old.DayOfWeek == d)))
                order.DeliveryDays.Add(new OrderDeliveryDay { Id = Guid.NewGuid(), OrderId = id, DayOfWeek = weekday });
            order.DeliveryDaysPerWeek = dates.ServiceWeekdays.Count;
            await ValidateScheduleAsync(order, today, ct);
            order.FulfillmentBlockedReason = null;
        }
        else
        {
            var next = action switch { "approve" => "CONFIRMED", "reject" => "REJECTED", "hold" => "ON_HOLD", "resume" => "PENDING_APPROVAL", "cancel" => "CANCELLED", "activate" => "ACTIVE", "complete" => "COMPLETED", _ => "" };
            if (!OrderWorkflowPolicy.CanTransition(previous, next)) throw Error(409, "invalid_transition", $"Cannot {action} an order with status {previous}.");
            if (action == "approve")
            {
                await ValidateScheduleAsync(order, today, ct);
                await ValidatePackageAsync(order, now, ct);
                order.ApprovedAt = now; order.ApprovedBy = actor; order.HoldReason = null;
                eventCode = "ORDER_APPROVED";
            }
            if (action == "reject") { order.RejectedAt = now; order.RejectedBy = actor; order.RejectionReason = reason; eventCode = "ORDER_REJECTED"; }
            if (action == "hold") { order.HoldReason = reason; eventCode = "ORDER_ON_HOLD"; }
            if (action == "resume") order.HoldReason = null;
            if (action == "activate" && (!OrderWorkflowPolicy.CanFulfill(order) || order.StartDate > today || order.EndDate < today))
                throw Error(409, "activation_not_allowed", "Staff approval, payment eligibility and a started service period are required.");
            if (action == "complete" && (await db.Set<SubscriptionServiceDay>().CountAsync(d => d.OrderId == id, ct) !=
                SubscriptionMenuRules.ServiceDates(order.StartDate, order.EndDate, order.DeliveryDays.Select(d => d.DayOfWeek).ToHashSet()).Count() ||
                await db.Set<SubscriptionServiceDay>().AnyAsync(d => d.OrderId == id && d.Status != "DELIVERED", ct)))
                throw Error(409, "fulfillment_incomplete", "Every service day must be delivered before completion.");
            if (action == "cancel" && (previous == "CONFIRMED" && order.StartDate <= today || await db.Set<SubscriptionServiceDay>().AnyAsync(d => d.OrderId == id &&
                (d.SelectionLocked || d.Status == "DELIVERED" || d.Status == "PREPARING" || d.Status == "OUT_FOR_DELIVERY"), ct)))
                throw Error(409, "cancellation_review_required", "Cancellation is unavailable after the service period or preparation starts. Contact Diet Time.");
            order.Status = next;
            if (next is "REJECTED" or "CANCELLED" && order.PaymentStatus is "PAID" or "PARTIALLY_REFUNDED") order.PaymentStatus = PaymentStatuses.RefundPending;
        }
        order.RowVersion++; order.UpdatedAt = now; order.UpdatedBy = actor;
        var history = new OrderStatusHistory { Id = Guid.NewGuid(), OrderId = id, PreviousStatus = previous, Status = order.Status,
            PreviousPaymentStatus = previousPayment, PaymentStatus = order.PaymentStatus, ChangedBy = actor, ChangedAt = now, Notes = reason ?? action };
        db.OrderStatusHistory.Add(history);
        if (eventCode != null) OrderNotificationEvents.Add(db, order, history, eventCode, now);
        await db.SaveChangesAsync(ct);
        if (OrderWorkflowPolicy.CanFulfill(order)) await meals.InitializeAsync(order, ct);
        await tx.CommitAsync(ct);
        if (eventCode != null) notifications.TryEnqueue(id);
        return (await GetAsync(id, ct))!;
    }

    private async Task ValidateScheduleAsync(Order o, DateOnly today, CancellationToken ct)
    {
        if (o.StartDate < today.AddDays(Math.Max(1, options.Value.MinimumLeadTimeDays)) || o.StartDate.DayOfWeek == DayOfWeek.Friday ||
            !o.DeliveryDays.Any(d => d.DayOfWeek == OrderSchedulingRules.ToApiWeekday(o.StartDate.DayOfWeek)))
            throw Error(409, "start_date_conflict", "The start date is no longer valid. Explicitly revise the dates with the customer before approval.");
        var days = await db.MealPlanTemplateDays.Where(d => d.MealPlanTemplateId == o.MealPlanTemplateId && d.IsActive).Select(d => d.MenuWeekday).ToArrayAsync(ct);
        var allowed = days.Select(d => d switch { MenuWeekday.Monday => 1, MenuWeekday.Tuesday => 2, MenuWeekday.Wednesday => 3, MenuWeekday.Thursday => 4, MenuWeekday.Saturday => 6, MenuWeekday.Sunday => 7, _ => 5 }).ToHashSet();
        if (o.DeliveryDays.Any(d => d.DayOfWeek == 5 || !allowed.Contains(d.DayOfWeek))) throw Error(409, "schedule_conflict", "The selected service weekdays are no longer available. Revise dates explicitly.");
    }

    private async Task ValidatePackageAsync(Order o, DateTimeOffset now, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM public.meal_plan_prices WHERE id = {o.MealPlanPriceId} FOR SHARE", ct);
        var price = await db.MealPlanPrices.AsNoTracking().SingleOrDefaultAsync(p => p.Id == o.MealPlanPriceId, ct);
        if (price is null || !await PurchasablePlanPrices.Query(db, now).AnyAsync(p => p.Id == o.MealPlanPriceId, ct))
            throw Error(409, "package_unavailable", "The selected package is inactive or unavailable. Review this order with the customer.");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT code FROM public.meal_plan_price_packages WHERE code = {price.PackageCode} FOR SHARE", ct);
        if (o.FulfillmentMethod == FulfillmentMethods.HomeDelivery)
        {
            if (!await db.MealPlanPricePackages.AnyAsync(p => p.Code == price.PackageCode && p.IsDeliveryEnabled && p.IsActive, ct) ||
                !await db.CustomerAddresses.AnyAsync(a => a.Id == o.CustomerAddressId && a.CustomerProfileId == o.CustomerProfileId && a.IsActive, ct) ||
                !await db.DeliveryTimeSlots.AnyAsync(s => s.Id == o.DeliveryTimeSlotId && s.IsActive, ct))
                throw Error(409, "delivery_unavailable", "The package, delivery address or time slot is no longer available.");
        }
        else
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM public.takeaway_branches WHERE id = {o.TakeawayBranchId} FOR SHARE", ct);
            if (!await db.TakeawayBranches.AnyAsync(b => b.Id == o.TakeawayBranchId && b.IsActive, ct)) throw Error(409, "branch_unavailable", "The selected takeaway branch is inactive. Review the order with the customer.");
        }
    }
    private static OrderReviewException Error(int status, string code, string detail) => new(status, code, detail);
}
