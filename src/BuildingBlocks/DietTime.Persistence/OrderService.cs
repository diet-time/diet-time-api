using System.Data;
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
}

public sealed class OrderService(
    DietTimeDbContext db,
    TimeProvider clock,
    IOptions<OrderOptions> options,
    ILogger<OrderService> logger) : IOrderService
{
    public async Task<PlaceOrderResult> PlaceAsync(
        PlaceOrderRequest request,
        string idempotencyKey,
        Guid userId,
        CancellationToken cancellationToken)
    {
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
            return new(PlaceOrderStatus.Replayed, replay);
        }
        if (await db.Orders.AsNoTracking().AnyAsync(x => x.IdempotencyKey == key, cancellationToken))
            return Fail(PlaceOrderStatus.IdempotencyConflict,
                "Idempotency-Key has already been used for a different order.");

        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var profile = await db.CustomerProfiles
            .SingleOrDefaultAsync(x => x.Id == request.CustomerProfileId && x.IsActive, cancellationToken);
        if (profile is null || profile.UserId != userId)
            return Fail(PlaceOrderStatus.CustomerNotFound, "Customer profile was not found or is not accessible.");

        var template = await db.MealPlanTemplates
            .Include(x => x.Translations)
            .Include(x => x.Days.Where(day => day.IsActive))
            .SingleOrDefaultAsync(x => x.Id == request.MealPlanTemplateId, cancellationToken);
        if (template is null)
            return Fail(PlaceOrderStatus.TemplateNotFound, "Meal plan template was not found.");
        if (!template.IsActive || !template.IsPublished || !template.IsLatest ||
            (template.ValidFrom.HasValue && today < template.ValidFrom.Value) ||
            (template.ValidUntil.HasValue && today > template.ValidUntil.Value))
            return Fail(PlaceOrderStatus.TemplateUnavailable, "Meal plan template is not currently available.");

        var price = await db.MealPlanPrices
            .Include(x => x.Translations)
            .SingleOrDefaultAsync(x => x.Id == request.MealPlanPriceId, cancellationToken);
        if (price is null || price.MealPlanTemplateId != template.Id)
            return Fail(PlaceOrderStatus.PriceNotFound, "Meal plan price was not found for the selected template.");
        if (!price.IsActive || price.EffectiveFrom > now ||
            (price.EffectiveUntil.HasValue && price.EffectiveUntil.Value <= now))
            return Fail(PlaceOrderStatus.PriceUnavailable, "Meal plan price is not currently effective.");

        if (request.CouponCode is not null)
            return Fail(PlaceOrderStatus.CouponNotSupported, "Coupons are not supported yet; couponCode must be null.");

        var requestedMealIds = request.Meals.Select(x => x.MealTypeId).ToArray();
        var mealTypes = await db.MealTypes.AsNoTracking()
            .Where(x => requestedMealIds.Contains(x.Id) && x.IsActive)
            .Select(x => new MealTypeRow(
                x.Id,
                x.Code,
                x.Translations.Where(t => t.LanguageCode == profile.PreferredLanguage).Select(t => t.Name).FirstOrDefault()
                    ?? x.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault()
                    ?? x.Code))
            .ToListAsync(cancellationToken);
        if (mealTypes.Count != requestedMealIds.Length)
            return Fail(PlaceOrderStatus.InvalidMealConfiguration, "One or more selected meal types do not exist or are inactive.");

        var supportedMealTypeIds = await db.MealPlanTemplateSlots.AsNoTracking()
            .Where(x => x.IsActive && x.Day.MealPlanTemplateId == template.Id && x.Day.IsActive)
            .Select(x => x.MealTypeId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (requestedMealIds.Any(id => !supportedMealTypeIds.Contains(id)))
            return Fail(PlaceOrderStatus.InvalidMealConfiguration, "A selected meal type is not offered by this meal plan.");

        var quantities = request.Meals.ToDictionary(x => x.MealTypeId, x => x.Quantity);
        var snackQuantity = mealTypes.Where(x => MealTypeClassification.IsSnack(x.Code))
            .Sum(x => quantities[x.Id]);
        var mealQuantity = request.Meals.Sum(x => x.Quantity) - snackQuantity;
        if (mealQuantity != price.MealsPerDay || snackQuantity != price.SnacksPerDay)
            return Fail(PlaceOrderStatus.InvalidMealConfiguration,
                $"Selection must contain {price.MealsPerDay} meal(s) and {price.SnacksPerDay} snack(s) per day.");

        var configuredDays = template.Days.Select(x => ToApiWeekday(x.MenuWeekday)).ToHashSet();
        if (request.DeliveryDays.Any(day => !configuredDays.Contains(day)))
            return Fail(PlaceOrderStatus.InvalidDeliveryDays, "A selected delivery weekday is not configured for this plan.");

        var address = await db.CustomerAddresses.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == request.CustomerAddressId && x.CustomerProfileId == profile.Id && x.IsActive,
            cancellationToken);
        if (address is null)
            return Fail(PlaceOrderStatus.AddressNotFound, "The selected active address was not found for this customer.");

        var slot = await db.DeliveryTimeSlots.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == request.DeliveryTimeSlotId && x.IsActive, cancellationToken);
        if (slot is null)
            return Fail(PlaceOrderStatus.DeliveryTimeSlotNotFound, "The selected delivery time slot was not found or is inactive.");

        var minimumStartDate = today.AddDays(Math.Max(0, options.Value.MinimumLeadTimeDays));
        if (request.StartDate < minimumStartDate)
            return Fail(PlaceOrderStatus.InvalidStartDate,
                $"startDate must be on or after {minimumStartDate:yyyy-MM-dd}.");
        if (!request.DeliveryDays.Contains(OrderSchedulingRules.ToApiWeekday(request.StartDate.DayOfWeek)))
            return Fail(PlaceOrderStatus.InvalidStartDate, "startDate must fall on a selected delivery weekday.");
        if (price.DurationDays <= 0)
            return Fail(PlaceOrderStatus.PriceUnavailable, "The selected price has an invalid service duration.");

        var endDate = OrderSchedulingRules.CalculateEndDate(
            request.StartDate, request.DeliveryDays, price.DurationDays);
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
            CustomerAddressId = address.Id,
            DeliveryTimeSlotId = slot.Id,
            StartDate = request.StartDate,
            EndDate = endDate,
            DeliveryDaysPerWeek = request.DeliveryDays.Count,
            PlanName = planName,
            PlanDurationName = durationName,
            Subtotal = price.Amount,
            DiscountAmount = 0m,
            DeliveryCharge = 0m,
            TotalAmount = price.Amount,
            CurrencyCode = string.IsNullOrWhiteSpace(price.CurrencyCode) ? "QAR" : price.CurrencyCode.Trim().ToUpperInvariant(),
            CouponCode = null,
            DeliveryAddressName = address.AddressName,
            DeliveryAddressType = address.AddressType,
            DeliveryBuildingNo = address.BuildingNo,
            DeliveryStreetNo = address.StreetNo,
            DeliveryUnitNumber = address.UnitNumber,
            DeliveryZoneNo = address.ZoneNo,
            DeliveryArea = address.Area,
            DeliveryDirections = address.Directions,
            DeliveryLatitude = address.Latitude,
            DeliveryLongitude = address.Longitude,
            DeliveryFormattedAddress = address.FormattedAddress,
            DeliveryTimeSlotName = slot.Name,
            DeliveryStartTime = slot.StartTime,
            DeliveryEndTime = slot.EndTime,
            Status = OrderStatuses.Confirmed,
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
        order.StatusHistory.Add(new OrderStatusHistory
        {
            Id = Guid.NewGuid(), OrderId = order.Id, Status = OrderStatuses.Confirmed,
            Notes = "Order placed by customer", ChangedAt = now
        });

        db.Orders.Add(order);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Order {OrderNumber} placed for customer profile {CustomerProfileId}",
            order.OrderNumber, order.CustomerProfileId);
        return new(PlaceOrderStatus.Created, Map(order));
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
        var items = await query.OrderByDescending(x => x.PlacedAt).ThenByDescending(x => x.Id)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize)
            .Select(x => new CustomerOrderSummaryResponse(
                x.Id, x.OrderNumber, x.PlanName, x.PlanDurationName, x.StartDate, x.EndDate,
                x.Status, x.PaymentStatus, x.TotalAmount, x.CurrencyCode.Trim(), x.PlacedAt))
            .ToListAsync(cancellationToken);
        return new(items, pageNumber, pageSize, count);
    }

    private static async Task<PlaceOrderResponse?> LoadOrderAsync(
        IQueryable<Order> query, CancellationToken cancellationToken)
    {
        var order = await query.AsNoTracking()
            .Include(x => x.Meals)
            .Include(x => x.DeliveryDays)
            .SingleOrDefaultAsync(cancellationToken);
        return order is null ? null : Map(order);
    }

    private static PlaceOrderResponse Map(Order order) => new(
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
            new(order.DeliveryTimeSlotId, order.DeliveryTimeSlotName, order.DeliveryStartTime, order.DeliveryEndTime),
            new(order.CustomerAddressId, order.DeliveryAddressName, order.DeliveryAddressType,
                order.DeliveryBuildingNo, order.DeliveryStreetNo, order.DeliveryUnitNumber,
                order.DeliveryZoneNo, order.DeliveryArea, order.DeliveryDirections,
                order.DeliveryLatitude, order.DeliveryLongitude, order.DeliveryFormattedAddress)),
        new(order.Subtotal, order.DiscountAmount, order.DeliveryCharge, order.TotalAmount,
            order.CurrencyCode.Trim()),
        order.PlacedAt);

    private static int ToApiWeekday(MenuWeekday weekday) => weekday switch
    {
        MenuWeekday.Monday => 1, MenuWeekday.Tuesday => 2, MenuWeekday.Wednesday => 3,
        MenuWeekday.Thursday => 4, MenuWeekday.Friday => 5, MenuWeekday.Saturday => 6,
        MenuWeekday.Sunday => 7, _ => throw new ArgumentOutOfRangeException(nameof(weekday))
    };

    private static string FirstNonEmpty(params string?[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value))!.Trim();

    private static string LocalizedName(IEnumerable<NameRow> names, string language, string fallback)
    {
        var values = names.ToArray();
        return values.FirstOrDefault(x => x.LanguageCode.Equals(language, StringComparison.OrdinalIgnoreCase))?.Name
            ?? values.FirstOrDefault(x => x.LanguageCode.Equals("en", StringComparison.OrdinalIgnoreCase))?.Name
            ?? values.FirstOrDefault()?.Name
            ?? fallback;
    }

    private static PlaceOrderResult Fail(PlaceOrderStatus status, string detail) => new(status, null, detail);
    private sealed record MealTypeRow(Guid Id, string Code, string Name);
    private sealed record NameRow(string LanguageCode, string Name);
}
