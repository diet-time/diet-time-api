using System.Data;
using System.Globalization;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DietTime.Persistence;

public sealed class OrderOptions
{
    public const string SectionName = "Orders";
    public int MinimumLeadTimeDays { get; set; } = 1;
    public bool RequiresPrepayment { get; set; } // Default preserves the existing offline payment policy.
}

public sealed class OrderService(
    DietTimeDbContext db,
    TimeProvider clock,
    IOptions<OrderOptions> options,
    IOrderNotificationQueue notifications,
    ILogger<OrderService> logger) : IOrderService
{
    public async Task<PlaceOrderResult> PlaceAsync(
        PlaceOrderRequest request,
        string idempotencyKey,
        Guid userId,
        CancellationToken cancellationToken)
    {
        Guid? packageOptionId = null;
        string? durationCode = null;
        int? durationDays = null;
        PlaceOrderResult Fail(PlaceOrderStatus status, string detail, string? code = null)
        {
            logger.LogWarning("Order validation rejected. PlanId={PlanId} PackageOptionId={PackageOptionId} PriceId={PriceId} DurationCode={DurationCode} DurationDays={DurationDays} Code={Code} Condition={Condition}",
                request.MealPlanTemplateId, packageOptionId, request.MealPlanPriceId, durationCode, durationDays, code ?? status.ToString(), detail);
            return new(status, null, detail, code);
        }
        var key = idempotencyKey.Trim();
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);

        // Serialize requests for the same key before checking it. The unique index is
        // the final safeguard; the advisory lock makes concurrent replays deterministic.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken);

        var replay = await LoadOrderAsync(
            db.Orders.Where(x => x.IdempotencyKey == key &&
                x.CustomerProfileId == request.CustomerProfileId &&
                db.CustomerProfiles.Any(p => p.Id == x.CustomerProfileId && p.UserId == userId && p.IsActive)),
            cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("WhatsApp new-order notification skipped for replayed order. OrderId={OrderId} Reason=idempotent_replay", replay.Id);
            return new(PlaceOrderStatus.Replayed, replay);
        }
        if (await db.Orders.AsNoTracking().AnyAsync(x => x.IdempotencyKey == key, cancellationToken))
            return Fail(PlaceOrderStatus.IdempotencyConflict,
                "Idempotency-Key has already been used for a different order.");

        var now = clock.GetUtcNow();
        var today = SubscriptionMenuRules.QatarToday(now);
        var profile = await db.CustomerProfiles
            .SingleOrDefaultAsync(x => x.Id == request.CustomerProfileId && x.IsActive, cancellationToken);
        if (profile is null || profile.UserId != userId)
            return Fail(PlaceOrderStatus.CustomerNotFound, "Customer profile was not found or is not accessible.");

        if (!CustomerQuestionnaireRules.IsComplete(profile, today))
            return Fail(PlaceOrderStatus.QuestionnaireIncomplete, "Complete your questionnaire before placing an order.");

        var template = await db.MealPlanTemplates
            .Include(x => x.Translations)
            .Include(x => x.Days.Where(day => day.IsActive))
            .SingleOrDefaultAsync(x => x.Id == request.MealPlanTemplateId, cancellationToken);
        if (template is null)
            return Fail(PlaceOrderStatus.TemplateNotFound, "Meal plan template was not found.", "plan_not_found");
        if (!template.IsActive || !template.IsPublished ||
            await db.MealPlanTemplates.AnyAsync(version => version.VersionGroupId == template.VersionGroupId &&
                version.IsPublished && version.VersionNumber > template.VersionNumber, cancellationToken) ||
            (template.ValidFrom.HasValue && today < template.ValidFrom.Value) ||
            (template.ValidUntil.HasValue && today > template.ValidUntil.Value))
            return Fail(PlaceOrderStatus.TemplateUnavailable, "Meal plan template is not currently available.",
                !template.IsActive ? "plan_inactive" : !template.IsPublished ? "plan_not_published" :
                (template.ValidFrom.HasValue && today < template.ValidFrom.Value || template.ValidUntil.HasValue && today > template.ValidUntil.Value)
                    ? "plan_not_effective" : "plan_version_changed");

        // Keep the checked price stable until the agreed snapshot is committed.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM public.meal_plan_prices WHERE id = {request.MealPlanPriceId} FOR SHARE", cancellationToken);
        var price = await db.MealPlanPrices.AsNoTracking()
            .Include(x => x.Translations)
            .Include(x => x.PackageOption)
            .SingleOrDefaultAsync(x => x.Id == request.MealPlanPriceId, cancellationToken);
        if (price is null)
            return Fail(PlaceOrderStatus.PriceNotFound, "The selected pricing record was not found.", "pricing_not_available");
        packageOptionId = price.PackageOptionId; durationCode = price.PackageCode; durationDays = price.DurationDays;
        if (price.MealPlanTemplateId != template.Id)
            return Fail(PlaceOrderStatus.InvalidMealConfiguration, "The selected pricing record does not belong to this meal plan.", "invalid_plan_configuration");
        if (!price.IsActive || price.EffectiveFrom > now ||
            (price.EffectiveUntil.HasValue && price.EffectiveUntil.Value <= now))
            return Fail(PlaceOrderStatus.PriceUnavailable, "Meal plan price is not currently effective.", "pricing_not_available");

        if (!await PurchasablePlanPrices.Query(db, now).AnyAsync(item => item.Id == price.Id, cancellationToken))
        {
            if (price.PackageOption is null || !price.PackageOption.IsActive)
                return Fail(PlaceOrderStatus.PriceUnavailable, "The selected meal package is unavailable.", "package_not_available");
            if (price.PackageOption.MealCount != price.MealsPerDay || price.PackageOption.SnackCount != price.SnacksPerDay)
                return Fail(PlaceOrderStatus.PriceUnavailable, "The pricing quantities do not match the selected meal package.", "invalid_plan_configuration");
            if (!await db.MealPlanPricePackages.AnyAsync(p => p.Code == price.PackageCode && p.IsActive && p.DurationDays == price.DurationDays, cancellationToken))
                return Fail(PlaceOrderStatus.PriceUnavailable, "The selected package duration is unavailable.", "package_not_available");
            return Fail(PlaceOrderStatus.PriceUnavailable, "The selected pricing configuration is unavailable.", "invalid_plan_configuration");
        }

        if (!FulfillmentMethods.IsValid(request.FulfillmentMethod))
            return Fail(PlaceOrderStatus.InvalidFulfillmentMethod, "Choose TAKEAWAY or HOME_DELIVERY.");
        // Keep eligibility stable until the new order snapshot is committed.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT code FROM public.meal_plan_price_packages WHERE code = {price.PackageCode} FOR SHARE", cancellationToken);
        var duration = await db.MealPlanPricePackages.AsNoTracking().SingleAsync(
            x => x.Code == price.PackageCode, cancellationToken);
        if (!duration.IsActive || duration.DurationDays != price.DurationDays)
            return Fail(PlaceOrderStatus.PriceUnavailable, "The selected package is no longer purchasable.", "package_not_available");
        if (!FulfillmentMethods.IsAllowed(duration.IsDeliveryEnabled, request.FulfillmentMethod))
            return Fail(PlaceOrderStatus.DeliveryDisabled, "This package is available for takeaway only.");
        var isDelivery = request.FulfillmentMethod == FulfillmentMethods.HomeDelivery;
        TakeawayBranch? branch = null;
        if (!isDelivery)
        {
            // Prevent an administrator deactivating the selected branch during placement.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT id FROM public.takeaway_branches WHERE id = {request.TakeawayBranchId} FOR SHARE", cancellationToken);
            branch = await db.TakeawayBranches.AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == request.TakeawayBranchId && x.IsActive, cancellationToken);
            if (branch is null)
                return Fail(PlaceOrderStatus.InvalidTakeawayBranch, "Choose an active takeaway branch.");
        }

        var currentPrice = PackagePriceRules.Calculate(price.Amount, price.DiscountedPrice);
        if ((request.ExpectedTotalAmount.HasValue && request.ExpectedTotalAmount != currentPrice.EffectivePrice) ||
            (request.ExpectedRegularPrice.HasValue && request.ExpectedRegularPrice != currentPrice.RegularPrice) ||
            (request.ExpectedDiscountAmount.HasValue && request.ExpectedDiscountAmount != currentPrice.DiscountAmount) ||
            (request.ExpectedCurrencyCode != null && !string.Equals(request.ExpectedCurrencyCode.Trim(), price.CurrencyCode.Trim(), StringComparison.OrdinalIgnoreCase)))
            return Fail(PlaceOrderStatus.PriceChanged, "The plan price has changed. Please review the updated total before placing your order.");

        if (request.CouponCode is not null)
            return Fail(PlaceOrderStatus.CouponNotSupported, "Coupons are not supported yet; couponCode must be null.");

        // Purchase quantities are authoritative package data, not daily dish choices.
        if (price.MealsPerDay is < 1 or > 5 || price.SnacksPerDay < 0)
            return Fail(PlaceOrderStatus.PriceUnavailable, "The package has invalid meal quantities.");
        var mealTypes = await db.MealTypes.AsNoTracking()
            .Where(x => x.IsActive)
            .OrderByDescending(x => db.MealPlanTemplateSlots.Any(slot =>
                slot.MealTypeId == x.Id && slot.Day.MealPlanTemplateId == template.Id && slot.IsActive))
            .ThenBy(x => x.Code).ThenBy(x => x.Id)
            .Select(x => new MealTypeRow(x.Id, x.Code,
                x.Translations.Where(t => t.LanguageCode == profile.PreferredLanguage).Select(t => t.Name).FirstOrDefault()
                ?? x.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault() ?? x.Code))
            .ToListAsync(cancellationToken);
        // Reuse order_meals as a quantity snapshot. A single type can represent five
        // regular dishes; it does not constrain which slots a customer may choose.
        var regular = mealTypes.FirstOrDefault(t => !MealTypeClassification.IsSnack(t.Code));
        var snack = mealTypes.FirstOrDefault(t => MealTypeClassification.IsSnack(t.Code));
        if (regular is null || price.SnacksPerDay > 0 && snack is null)
            return Fail(PlaceOrderStatus.InvalidMealConfiguration, "The package's meal categories need configuration.");
        var quantities = new Dictionary<Guid, int> { [regular.Id] = price.MealsPerDay };
        if (price.SnacksPerDay > 0) quantities[snack!.Id] = price.SnacksPerDay;
        mealTypes = mealTypes.Where(t => quantities.ContainsKey(t.Id)).ToList();

        if (request.DeliveryDays.Contains(5))
            return Fail(PlaceOrderStatus.InvalidDeliveryDays, "Friday is a non-delivery day.");
        var configuredDays = template.Days.Select(x => ToApiWeekday(x.MenuWeekday)).ToHashSet();
        if (request.DeliveryDays.Any(day => !configuredDays.Contains(day)))
            return Fail(PlaceOrderStatus.InvalidDeliveryDays, "A selected delivery weekday is not configured for this plan.");

        var address = !isDelivery ? null : await db.CustomerAddresses.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == request.CustomerAddressId && x.CustomerProfileId == profile.Id && x.IsActive,
            cancellationToken);
        if (isDelivery && address is null)
            return Fail(PlaceOrderStatus.AddressNotFound, "The selected active address was not found for this customer.");

        var slot = !isDelivery ? null : await db.DeliveryTimeSlots.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == request.DeliveryTimeSlotId && x.IsActive, cancellationToken);
        if (isDelivery && slot is null)
            return Fail(PlaceOrderStatus.DeliveryTimeSlotNotFound, "The selected delivery time slot was not found or is inactive.");

        var minimumStartDate = today.AddDays(Math.Max(1, options.Value.MinimumLeadTimeDays));
        if (request.StartDate.DayOfWeek == DayOfWeek.Friday)
            return Fail(PlaceOrderStatus.InvalidStartDate, "Friday is not a delivery day.");
        if (request.StartDate < minimumStartDate)
            return Fail(PlaceOrderStatus.InvalidStartDate,
                $"startDate must be on or after {minimumStartDate:yyyy-MM-dd}.");
        if (!request.DeliveryDays.Contains(OrderSchedulingRules.ToApiWeekday(request.StartDate.DayOfWeek)))
            return Fail(PlaceOrderStatus.InvalidStartDate, "startDate must fall on a selected delivery weekday.");
        if (price.DurationDays <= 0)
            return Fail(PlaceOrderStatus.PriceUnavailable, "The selected price has an invalid service duration.");

        var endDate = OrderSchedulingRules.CalculateEndDate(
            request.StartDate, request.DeliveryDays, price.DurationDays);
        var serviceDates = SubscriptionMenuRules.ServiceDates(request.StartDate, endDate, request.DeliveryDays.ToHashSet()).ToArray();
        if (request.ServiceDates is not null && !request.ServiceDates.SequenceEqual(serviceDates))
            return Fail(PlaceOrderStatus.InvalidStartDate, "The selected service dates do not match this package and its service weekdays. Please review your dates.");
        var planName = LocalizedName(template.Translations.Select(x => new NameRow(x.LanguageCode, x.Name)),
            profile.PreferredLanguage, template.Code);
        var durationName = LocalizedName(price.Translations.Select(x => new NameRow(x.LanguageCode, x.Name)),
            profile.PreferredLanguage, $"{price.DurationDays} Days");
        if (price.Translations.Count == 0)
        {
            durationName = await db.MealPlanPricePackages.AsNoTracking()
                .Where(x => x.DurationDays == price.DurationDays && x.IsActive)
                .OrderBy(x => x.DisplayOrder)
                .Select(x => profile.PreferredLanguage == "ar" ? x.NameAr : x.NameEn)
                .FirstOrDefaultAsync(cancellationToken) ?? durationName;
        }

        var sequence = await db.Database.SqlQueryRaw<long>(
            "SELECT nextval('public.order_number_seq') AS \"Value\"").SingleAsync(cancellationToken);
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OrderNumber = $"ORD-{now.UtcDateTime:yyyyMMdd}-{sequence:000000}",
            CustomerProfileId = profile.Id,
            MealPlanTemplateId = template.Id,
            MealPlanPriceId = price.Id,
            CustomerAddressId = address?.Id,
            FulfillmentMethod = request.FulfillmentMethod,
            TakeawayBranchId = branch?.Id,
            TakeawayBranchName = branch?.Name,
            TakeawayBranchAddress = branch?.Address,
            DeliveryTimeSlotId = slot?.Id,
            StartDate = request.StartDate,
            EndDate = endDate,
            DeliveryDaysPerWeek = request.DeliveryDays.Count,
            PlanName = planName,
            PlanDurationName = durationName,
            Subtotal = price.Amount,
            DiscountAmount = currentPrice.DiscountAmount,
            DeliveryCharge = 0m,
            TotalAmount = currentPrice.EffectivePrice,
            CurrencyCode = string.IsNullOrWhiteSpace(price.CurrencyCode) ? "QAR" : price.CurrencyCode.Trim().ToUpperInvariant(),
            CouponCode = null,
            DeliveryAddressName = address?.AddressName,
            DeliveryAddressType = address?.AddressType ?? CustomerAddressTypes.Other,
            DeliveryBuildingNo = address?.BuildingNo,
            DeliveryStreetNo = address?.StreetNo,
            DeliveryUnitNumber = address?.UnitNumber,
            DeliveryZoneNo = address?.ZoneNo,
            DeliveryArea = address?.Area ?? string.Empty,
            DeliveryDirections = address?.Directions,
            DeliveryLatitude = address?.Latitude,
            DeliveryLongitude = address?.Longitude,
            DeliveryFormattedAddress = address?.FormattedAddress,
            DeliveryTimeSlotName = slot?.Name ?? string.Empty,
            DeliveryStartTime = slot?.StartTime ?? default,
            DeliveryEndTime = slot?.EndTime ?? default,
            Status = OrderStatuses.PendingApproval,
            RequiresPrepayment = options.Value.RequiresPrepayment,
            PaymentStatus = PaymentStatuses.Pending,
            PlacedAt = now,
            IdempotencyKey = key,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = userId,
            UpdatedBy = userId,
            RowVersion = 1
        };
        foreach (var meal in mealTypes)
            order.Meals.Add(new OrderMeal
            {
                Id = Guid.NewGuid(), OrderId = order.Id, MealTypeId = meal.Id,
                MealTypeName = meal.Name, Quantity = quantities[meal.Id]
            });
        foreach (var day in request.DeliveryDays.Order())
            order.DeliveryDays.Add(new OrderDeliveryDay
                { Id = Guid.NewGuid(), OrderId = order.Id, DayOfWeek = day });
        var submitted = new OrderStatusHistory
        {
            Id = Guid.NewGuid(), OrderId = order.Id, Status = OrderStatuses.PendingApproval,
            Notes = "Order submitted for staff approval", ChangedAt = now, ChangedBy = userId
        };
        order.StatusHistory.Add(submitted);

        db.Orders.Add(order);
        OrderNotificationEvents.Add(db, order, submitted, "ORDER_RECEIVED", now, staff: true);
        await db.SaveChangesAsync(cancellationToken);
        // The order retains the full agreed schedule. Service days and default meals are
        // initialized only after staff approval and any required prepayment.
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Order {OrderNumber} placed for customer profile {CustomerProfileId}",
            order.OrderNumber, order.CustomerProfileId);
        if (!notifications.TryEnqueue(order.Id))
            logger.LogWarning("Notification wake-up queue full or stopping; durable order events remain pending. OrderId={OrderId}", order.Id);
        return new(PlaceOrderStatus.Created, Map(order, mealTypes.ToDictionary(x => x.Id, x => x.Code)));
    }

    public Task<PlaceOrderResponse?> GetAsync(
        Guid orderId, Guid userId, CancellationToken cancellationToken) =>
        LoadOrderAsync(db.Orders.AsNoTracking().Where(x =>
            x.Id == orderId && db.CustomerProfiles.Any(p =>
                p.Id == x.CustomerProfileId && p.UserId == userId && p.IsActive)), cancellationToken);

    public async Task<CustomerOrdersResponse?> GetCustomerOrdersAsync(
        Guid customerProfileId,
        Guid userId,
        string? status,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken)
    {
        if (!await db.CustomerProfiles.AsNoTracking().AnyAsync(
                x => x.Id == customerProfileId && x.UserId == userId && x.IsActive, cancellationToken))
            return null;

        var query = db.Orders.AsNoTracking().Where(x => x.CustomerProfileId == customerProfileId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalized = status.Trim().ToUpperInvariant();
            query = query.Where(x => x.Status == normalized);
        }
        var count = await query.CountAsync(cancellationToken);
        var page = await query.Include(x => x.Meals).OrderByDescending(x => x.PlacedAt).ThenByDescending(x => x.Id)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        var typeIds = page.SelectMany(x => x.Meals).Select(x => x.MealTypeId).Distinct().ToArray();
        var codes = await db.MealTypes.AsNoTracking().Where(x => typeIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Code, cancellationToken);
        var items = page.Select(x => new CustomerOrderSummaryResponse(
                x.Id, x.OrderNumber, x.PlanName, x.PlanDurationName, x.StartDate, x.EndDate,
                x.Status, x.PaymentStatus, x.TotalAmount, x.CurrencyCode.Trim(), x.PlacedAt, x.FulfillmentMethod,
                x.TakeawayBranchId.HasValue ? new TakeawayBranchResponse(x.TakeawayBranchId.Value, x.TakeawayBranchName!, x.TakeawayBranchAddress) : null,
                EntitlementCount(x, codes, false), EntitlementCount(x, codes, true), MapReview(x))).ToArray();
        return new(items, pageNumber, pageSize, count);
    }

    private async Task<PlaceOrderResponse?> LoadOrderAsync(
        IQueryable<Order> query, CancellationToken cancellationToken)
    {
        var order = await query.AsNoTracking()
            .Include(x => x.Meals)
            .Include(x => x.DeliveryDays)
            .SingleOrDefaultAsync(cancellationToken);
        if (order is null) return null;
        var typeIds = order.Meals.Select(x => x.MealTypeId).Distinct().ToArray();
        var codes = await db.MealTypes.AsNoTracking().Where(x => typeIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Code, cancellationToken);
        return Map(order, codes);
    }

    internal static PlaceOrderResponse Map(Order order, IReadOnlyDictionary<Guid, string> codes) => new(
        order.Id,
        order.OrderNumber,
        order.Status,
        order.PaymentStatus,
        new(order.MealPlanTemplateId, order.MealPlanPriceId, order.PlanName, order.PlanDurationName),
        order.Meals.OrderBy(x => x.MealTypeName).Select(x =>
            new OrderMealResponse(x.MealTypeId, x.MealTypeName, x.Quantity)).ToArray(),
        new(order.DeliveryDaysPerWeek,
            order.DeliveryDays.OrderBy(x => x.DayOfWeek).Select(x => x.DayOfWeek).ToArray(),
            order.StartDate,
            order.EndDate,
            order.DeliveryTimeSlotId.HasValue ? new(order.DeliveryTimeSlotId.Value, order.DeliveryTimeSlotName, order.DeliveryStartTime, order.DeliveryEndTime) : null,
            order.CustomerAddressId.HasValue ? new(order.CustomerAddressId.Value, order.DeliveryAddressName, order.DeliveryAddressType,
                order.DeliveryBuildingNo, order.DeliveryStreetNo, order.DeliveryUnitNumber,
                order.DeliveryZoneNo, order.DeliveryArea, order.DeliveryDirections,
                order.DeliveryLatitude, order.DeliveryLongitude, order.DeliveryFormattedAddress) : null,
            SubscriptionMenuRules.ServiceDates(order.StartDate, order.EndDate, order.DeliveryDays.Select(x => x.DayOfWeek).ToHashSet()).ToArray()),
        new(order.Subtotal, order.DiscountAmount, order.DeliveryCharge, order.TotalAmount,
            order.CurrencyCode.Trim()),
        order.PlacedAt, order.FulfillmentMethod,
        order.TakeawayBranchId.HasValue ? new(order.TakeawayBranchId.Value, order.TakeawayBranchName!, order.TakeawayBranchAddress) : null,
        EntitlementCount(order, codes, false), EntitlementCount(order, codes, true), MapReview(order));

    internal static OrderReviewResponse MapReview(Order order) => new(order.RowVersion,
        order.ApprovedBy, order.ApprovedAt, order.RejectedBy, order.RejectedAt, order.RejectionReason,
        order.HoldReason, order.RequiresPrepayment,
        order.Status == OrderStatuses.Confirmed && order.RequiresPrepayment && order.PaymentStatus != PaymentStatuses.Paid,
        OrderWorkflowPolicy.CanFulfill(order), order.FulfillmentBlockedReason);

    // Count the purchased quantity snapshot, independent of localized slot names and current prices.
    private static int? EntitlementCount(Order order, IReadOnlyDictionary<Guid, string> codes, bool snack) =>
        order.Meals.Any(x => !codes.ContainsKey(x.MealTypeId)) ? null : order.Meals
            .Where(x => MealTypeClassification.IsSnack(codes[x.MealTypeId]) == snack).Sum(x => x.Quantity);

    private static int ToApiWeekday(MenuWeekday weekday) => weekday switch
    {
        MenuWeekday.Monday => 1, MenuWeekday.Tuesday => 2, MenuWeekday.Wednesday => 3,
        MenuWeekday.Thursday => 4, MenuWeekday.Friday => 5, MenuWeekday.Saturday => 6,
        MenuWeekday.Sunday => 7, _ => throw new ArgumentOutOfRangeException(nameof(weekday))
    };

    internal static string FormatDeliveryDays(Order order)
    {
        var configured = order.DeliveryDays.Select(x => x.DayOfWeek).ToHashSet();
        var seen = new HashSet<int>();
        var labels = new List<string>();
        for (var date = order.StartDate;
             date <= order.EndDate && seen.Count < configured.Count;
             date = date.AddDays(1))
        {
            var weekday = OrderSchedulingRules.ToApiWeekday(date.DayOfWeek);
            if (configured.Contains(weekday) && seen.Add(weekday))
                labels.Add(date.ToString("ddd", CultureInfo.InvariantCulture));
        }
        return string.Join(", ", labels);
    }

    internal static string FormatDeliveryAddress(Order order)
    {
        if (order.FulfillmentMethod == FulfillmentMethods.Takeaway)
            return string.IsNullOrWhiteSpace(order.TakeawayBranchName) ? "Takeaway — collect from Diet Time"
                : string.Join(", ", new[] { "Takeaway — " + order.TakeawayBranchName, order.TakeawayBranchAddress }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
        if (!string.IsNullOrWhiteSpace(order.DeliveryFormattedAddress))
            return order.DeliveryFormattedAddress.Trim();
        var parts = new[]
        {
            order.DeliveryAddressName,
            Labeled("Unit", order.DeliveryUnitNumber),
            Labeled("Building", order.DeliveryBuildingNo),
            Labeled("Street", order.DeliveryStreetNo),
            Labeled("Zone", order.DeliveryZoneNo),
            order.DeliveryArea
        };
        var displayAddress = string.Join(", ", parts.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase));
        return !string.IsNullOrWhiteSpace(displayAddress)
            ? displayAddress
            : order.DeliveryDirections?.Trim() ?? string.Empty;
    }

    private static string? Labeled(string label, string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : $"{label} {value.Trim()}";

    private static string LocalizedName(IEnumerable<NameRow> names, string language, string fallback)
    {
        var values = names.ToArray();
        return values.FirstOrDefault(x => x.LanguageCode.Equals(language, StringComparison.OrdinalIgnoreCase))?.Name
            ?? values.FirstOrDefault(x => x.LanguageCode.Equals("en", StringComparison.OrdinalIgnoreCase))?.Name
            ?? values.FirstOrDefault()?.Name
            ?? fallback;
    }

    private sealed record MealTypeRow(Guid Id, string Code, string Name);
    private sealed record NameRow(string LanguageCode, string Name);
}
