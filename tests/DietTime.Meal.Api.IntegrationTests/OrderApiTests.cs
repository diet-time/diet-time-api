using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DietTime.Domain;
using DietTime.Application;
using DietTime.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed class OrderApiTests : IAsyncLifetime
{
    private readonly bool enabled = Environment.GetEnvironmentVariable("RUN_INTEGRATION_TESTS") == "true";
    private PostgreSqlContainer? postgres;
    private ApiFactory? factory;
    private HttpClient? client;
    private Guid userId;
    private Guid otherUserId;
    private Guid profileId;
    private Guid planId;
    private Guid priceId;
    private Guid addressId;
    private Guid slotId;
    private Guid lunchId;
    private Guid dinnerId;
    private Guid snackId;
    private readonly WhatsAppRecorder whatsApp = new();

    public async Task InitializeAsync()
    {
        if (!enabled) return;
        postgres = new PostgreSqlBuilder().WithImage("postgres:16-alpine")
            .WithDatabase("diettime_orders_test").WithUsername("postgres").WithPassword("postgres").Build();
        await postgres.StartAsync();
        factory = new ApiFactory(postgres.GetConnectionString(), services =>
        {
            services.RemoveAll<IWhatsAppService>();
            services.AddSingleton(whatsApp);
            services.AddScoped<IWhatsAppService, CommitAwareWhatsAppService>();
        });
        client = factory.CreateClient();
        await SeedAsync();
        Authenticate(userId);
    }

    public async Task DisposeAsync()
    {
        client?.Dispose();
        if (factory is not null) await factory.DisposeAsync();
        if (postgres is not null) await postgres.DisposeAsync();
    }

    [Fact]
    public async Task Places_replays_and_reads_order_from_snapshots()
    {
        if (!enabled) return;
        const string key = "47c99616-1c75-4073-af37-91ecdf680957";
        var request = Request();
        using var firstMessage = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
            { Content = JsonContent.Create(request) };
        firstMessage.Headers.Add("Idempotency-Key", key);
        var first = await client!.SendAsync(firstMessage);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var created = await first.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = created.GetProperty("id").GetGuid();
        Assert.Equal("Classic", created.GetProperty("plan").GetProperty("name").GetString());
        Assert.Equal("1 Month", created.GetProperty("plan").GetProperty("durationName").GetString());
        Assert.Equal(1880m, created.GetProperty("pricing").GetProperty("totalAmount").GetDecimal());
        Assert.Equal(ExpectedEndDate(RequestStartDate(), 20),
            DateOnly.Parse(created.GetProperty("delivery").GetProperty("endDate").GetString()!));
        var notification = Assert.Single(whatsApp.Notifications);
        Assert.True(whatsApp.OrderWasCommittedWhenCalled);
        Assert.Equal("Ahmed Ali", notification.CustomerName);
        Assert.Equal("+97450123456", notification.CustomerMobile);
        Assert.Equal("Classic", notification.MealPlanName);
        Assert.Equal("1 Month", notification.Duration);
        Assert.Equal(2, notification.MealsPerDay);
        Assert.Equal(RequestStartDate(), notification.StartDate);
        Assert.Equal(ExpectedDeliveryDayLabels(RequestStartDate()), notification.DeliveryDays);
        Assert.Equal("Zone 91, Al Wakrah, Qatar", notification.DeliveryAddress);
        Assert.Equal(1880m, notification.TotalAmount);
        Assert.Equal("QAR", notification.Currency);
        Assert.Equal("CONFIRMED", notification.OrderStatus);

        using var replayMessage = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
            { Content = JsonContent.Create(request) };
        replayMessage.Headers.Add("Idempotency-Key", key);
        var replay = await client.SendAsync(replayMessage);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(orderId, (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        Assert.Single(whatsApp.Notifications);

        using (var scope = factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DietTimeDbContext>();
            Assert.Equal(1, await db.Orders.CountAsync());
            Assert.Equal(3, await db.OrderMeals.CountAsync());
            Assert.Equal(5, await db.OrderDeliveryDays.CountAsync());
            Assert.Equal(1, await db.OrderStatusHistory.CountAsync());
            var address = await db.CustomerAddresses.SingleAsync(x => x.Id == addressId);
            address.Area = "Changed Area";
            var slot = await db.DeliveryTimeSlots.SingleAsync(x => x.Id == slotId);
            slot.Name = "Changed Slot";
            await db.SaveChangesAsync();
        }

        var stored = await client.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{orderId}");
        Assert.Equal("Al Wakrah", stored.GetProperty("delivery").GetProperty("address").GetProperty("area").GetString());
        Assert.Equal("Morning", stored.GetProperty("delivery").GetProperty("timeSlot").GetProperty("name").GetString());

        var history = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/customer-profiles/{profileId}/orders?pageNumber=1&pageSize=20&status=confirmed");
        Assert.Equal(1, history.GetProperty("totalCount").GetInt32());
        Assert.Equal(orderId, history.GetProperty("items")[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Rejects_missing_key_coupon_and_foreign_customer()
    {
        if (!enabled) return;
        var missingKey = await client!.PostAsJsonAsync("/api/v1/orders", Request());
        Assert.Equal(HttpStatusCode.BadRequest, missingKey.StatusCode);

        using var couponMessage = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
            { Content = JsonContent.Create(Request("SAVE10")) };
        couponMessage.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.BadRequest, (await client!.SendAsync(couponMessage)).StatusCode);

        Authenticate(otherUserId);
        using var foreignMessage = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
            { Content = JsonContent.Create(Request()) };
        foreignMessage.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(foreignMessage)).StatusCode);
        Assert.Empty(whatsApp.Notifications);
    }

    [Fact]
    public async Task WhatsApp_failure_does_not_fail_or_duplicate_the_order()
    {
        if (!enabled) return;
        whatsApp.Result = new WhatsAppSendResult
        {
            Success = false,
            ErrorCode = "131030",
            ErrorMessage = "Recipient is not registered"
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
            { Content = JsonContent.Create(Request()) };
        message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client!.SendAsync(message);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Single(whatsApp.Notifications);
        using var scope = factory!.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider
            .GetRequiredService<DietTimeDbContext>().Orders.CountAsync());
    }

    [Fact]
    public async Task Development_test_endpoint_uses_the_configured_destination()
    {
        if (!enabled) return;

        var response = await client!.PostAsync(
            "/api/admin/integrations/whatsapp/test", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("97474452435", json.GetProperty("destination").GetString());
        Assert.Single(whatsApp.Notifications);
    }

    private object Request(string? coupon = null) => new
    {
        customerProfileId = profileId, mealPlanTemplateId = planId, mealPlanPriceId = priceId,
        customerAddressId = addressId, deliveryTimeSlotId = slotId, startDate = RequestStartDate(),
        deliveryDays = DeliveryDays(),
        meals = new[] { new { mealTypeId = lunchId, quantity = 1 }, new { mealTypeId = dinnerId, quantity = 1 }, new { mealTypeId = snackId, quantity = 1 } },
        couponCode = coupon
    };

    private static DateOnly RequestStartDate()
    {
        var date = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);
        while (!DeliveryDays().Contains(ApiWeekday(date.DayOfWeek))) date = date.AddDays(1);
        return date;
    }

    private static int[] DeliveryDays() => [1, 2, 3, 4, 5];
    private static int ApiWeekday(DayOfWeek day) => day == DayOfWeek.Sunday ? 7 : (int)day;
    private static DateOnly ExpectedEndDate(DateOnly start, int serviceDays)
    {
        var date = start;
        var count = 0;
        while (true)
        {
            if (DeliveryDays().Contains(ApiWeekday(date.DayOfWeek)) && ++count == serviceDays) return date;
            date = date.AddDays(1);
        }
    }

    private static string ExpectedDeliveryDayLabels(DateOnly start)
    {
        var seen = new HashSet<int>();
        var labels = new List<string>();
        for (var date = start; seen.Count < DeliveryDays().Length; date = date.AddDays(1))
        {
            var weekday = ApiWeekday(date.DayOfWeek);
            if (DeliveryDays().Contains(weekday) && seen.Add(weekday))
                labels.Add(date.ToString("ddd", System.Globalization.CultureInfo.InvariantCulture));
        }
        return string.Join(", ", labels);
    }

    private async Task SeedAsync()
    {
        using var scope = factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DietTimeDbContext>();
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        userId = Guid.NewGuid(); otherUserId = Guid.NewGuid(); profileId = Guid.NewGuid();
        planId = Guid.NewGuid(); priceId = Guid.NewGuid(); addressId = Guid.NewGuid(); slotId = Guid.NewGuid();
        lunchId = Guid.NewGuid(); dinnerId = Guid.NewGuid(); snackId = Guid.NewGuid();
        db.Users.AddRange(
            new ApplicationUser { Id = userId, UserName = "order-owner", PhoneNumber = "+97450123456", SecurityStamp = Guid.NewGuid().ToString() },
            new ApplicationUser { Id = otherUserId, UserName = "other-order-user", SecurityStamp = Guid.NewGuid().ToString() });
        db.CustomerProfiles.Add(new CustomerProfile { Id = profileId, UserId = userId, PreferredName = "Ahmed Ali", IsActive = true, PreferredLanguage = "en", OnboardingStatus = "COMPLETED", CreatedAt = now, UpdatedAt = now, RowVersion = 1 });
        var types = new[]
        {
            Type(lunchId, "LUNCH", "Lunch", now), Type(dinnerId, "DINNER", "Dinner", now),
            Type(snackId, "SNACK_DESSERT", "Snack / Dessert", now)
        };
        db.MealTypes.AddRange(types);
        var plan = new MealPlanTemplate { Id = planId, VersionGroupId = Guid.NewGuid(), VersionNumber = 1, IsLatest = true, Code = "CLASSIC", PlanType = "STANDARD", DurationDays = 20, IsPublished = true, IsActive = true, CreatedAt = now, UpdatedAt = now, RowVersion = 1 };
        plan.Translations.Add(new MealPlanTemplateTranslation { Id = Guid.NewGuid(), LanguageCode = "en", Name = "Classic", CreatedAt = now, UpdatedAt = now });
        var weekdays = Enum.GetValues<MenuWeekday>();
        for (var index = 0; index < weekdays.Length; index++)
        {
            var day = new MealPlanTemplateDay { Id = Guid.NewGuid(), Plan = plan, MenuWeekday = weekdays[index], DisplayOrder = index + 1, IsActive = true, CreatedAt = now, UpdatedAt = now };
            foreach (var type in types)
                day.Slots.Add(new MealPlanTemplateSlot { Id = Guid.NewGuid(), MealType = type, DisplayOrder = type.DisplayOrder, MinimumSelection = 0, MaximumSelection = 1, IsRequired = false, IsActive = true, CreatedAt = now, UpdatedAt = now, RowVersion = 1 });
            plan.Days.Add(day);
        }
        db.MealPlanTemplates.Add(plan);
        var price = new MealPlanPrice { Id = priceId, Plan = plan, DurationDays = 20, MealsPerDay = 2, SnacksPerDay = 1, CurrencyCode = "QAR", Amount = 1880m, EffectiveFrom = now.AddDays(-1), IsActive = true, CreatedAt = now, UpdatedAt = now };
        price.Translations.Add(new MealPlanPriceTranslation { Id = Guid.NewGuid(), LanguageCode = "en", Name = "1 Month", CreatedAt = now, UpdatedAt = now });
        db.MealPlanPrices.Add(price);
        db.CustomerAddresses.Add(new CustomerAddress { Id = addressId, CustomerProfileId = profileId, AddressName = "Home", AddressType = "HOME", BuildingNo = "126", StreetNo = "960", ZoneNo = "91", Area = "Al Wakrah", Directions = "Call me", FormattedAddress = "Zone 91, Al Wakrah, Qatar", IsDefault = true, IsActive = true, CreatedAt = now, UpdatedAt = now, RowVersion = 1 });
        db.DeliveryTimeSlots.Add(new DeliveryTimeSlot { Id = slotId, Code = "MORNING", Name = "Morning", NameAr = "Morning", StartTime = new(9, 0), EndTime = new(11, 0), SortOrder = 1, IsActive = true, CreatedAt = now, UpdatedAt = now, RowVersion = 1 });
        await db.SaveChangesAsync();
    }

    private static MealType Type(Guid id, string code, string name, DateTimeOffset now)
    {
        var type = new MealType { Id = id, Code = code, DisplayOrder = code == "SNACK_DESSERT" ? 3 : code == "DINNER" ? 2 : 1, IsActive = true, CreatedAt = now, UpdatedAt = now };
        type.Translations.Add(new MealTypeTranslation { Id = Guid.NewGuid(), LanguageCode = "en", Name = name, CreatedAt = now, UpdatedAt = now });
        return type;
    }

    private void Authenticate(Guid id)
    {
        client!.DefaultRequestHeaders.Remove("X-Development-User-Id");
        client.DefaultRequestHeaders.Add("X-Development-User-Id", id.ToString());
    }
}

internal sealed class WhatsAppRecorder
{
    public List<NewOrderWhatsAppNotification> Notifications { get; } = [];
    public bool OrderWasCommittedWhenCalled { get; set; }
    public WhatsAppSendResult Result { get; set; } = new()
    {
        Success = true,
        MessageId = "wamid.test"
    };
}

internal sealed class CommitAwareWhatsAppService(
    DietTimeDbContext db,
    WhatsAppRecorder recorder) : IWhatsAppService
{
    public async Task<WhatsAppSendResult> SendNewOrderNotificationAsync(
        NewOrderWhatsAppNotification notification,
        CancellationToken cancellationToken = default)
    {
        recorder.OrderWasCommittedWhenCalled =
            db.Database.CurrentTransaction is null &&
            await db.Orders.AsNoTracking().AnyAsync(
                x => x.Id == notification.OrderId, cancellationToken);
        recorder.Notifications.Add(notification);
        return recorder.Result;
    }
}
