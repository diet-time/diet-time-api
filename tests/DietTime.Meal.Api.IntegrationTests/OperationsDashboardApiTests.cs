using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using DietTime.Meal.Api.Controllers;
using DietTime.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed class OperationsDashboardApiTests : IAsyncLifetime
{
    private readonly bool enabled = Environment.GetEnvironmentVariable("RUN_INTEGRATION_TESTS") == "true";
    private PostgreSqlContainer? postgres;
    private ApiFactory? factory;
    private HttpClient? client;

    public async Task InitializeAsync()
    {
        if (!enabled) return;
        postgres = new PostgreSqlBuilder().WithImage("postgres:16-alpine")
            .WithDatabase("diettime_dashboard_test").WithUsername("postgres").WithPassword("postgres").Build();
        await postgres.StartAsync();
        factory = new ApiFactory(postgres.GetConnectionString());
        client = factory.CreateClient();
        await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        client?.Dispose();
        if (factory is not null) await factory.DisposeAsync();
        if (postgres is not null) await postgres.DisposeAsync();
    }

    [Fact]
    public async Task Rejects_invalid_dashboard_date_without_querying_storage()
    {
        var controller = new OperationsDashboardController(new NeverCalledDashboardService());

        var result = await controller.Get("13-08-2026");

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.IsType<ValidationProblemDetails>(problem.Value);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains("date", controller.ModelState.Keys);
    }

    [Fact]
    public async Task Returns_operational_workload_attention_and_actual_schedule_dates()
    {
        if (!enabled) return;

        var response = await client!.GetAsync("/api/admin/dashboard/operations?date=2026-08-13");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(2, body.GetProperty("today").GetProperty("scheduledDeliveries").GetInt32());
        Assert.Equal(1, body.GetProperty("today").GetProperty("customers").GetInt32());
        Assert.Equal(6, body.GetProperty("today").GetProperty("mealsToPrepare").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("today").GetProperty("completedDeliveries").ValueKind);
        Assert.Equal("2026-08-15", body.GetProperty("nextDeliveryDay").GetProperty("date").GetString());
        Assert.Equal(7, body.GetProperty("nextSevenDays").GetArrayLength());
        Assert.False(body.GetProperty("nextSevenDays")[1].GetProperty("hasDeliveries").GetBoolean());
        Assert.Equal(1, body.GetProperty("needsAttention").GetProperty("missingDeliveryAddresses").GetInt32());
        Assert.Equal(1, body.GetProperty("needsAttention").GetProperty("customersWithoutUpcomingDelivery").GetInt32());
        Assert.Equal(2, body.GetProperty("needsAttention").GetProperty("plansEndingSoon").GetInt32());
        Assert.Equal(2, body.GetProperty("todayDeliveries").GetArrayLength());
        Assert.Contains(body.GetProperty("upcomingPlanActivity").GetProperty("ending").EnumerateArray(),
            day => day.GetProperty("date").GetString() == "2026-08-15");
    }

    [Fact]
    public async Task Returns_empty_dashboard_and_rejects_invalid_date()
    {
        if (!enabled) return;

        var empty = await client!.GetFromJsonAsync<JsonElement>(
            "/api/admin/dashboard/operations?date=2030-01-01");
        Assert.Equal(0, empty.GetProperty("today").GetProperty("scheduledDeliveries").GetInt32());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("nextDeliveryDay").ValueKind);
        Assert.All(empty.GetProperty("nextSevenDays").EnumerateArray(),
            day => Assert.False(day.GetProperty("hasDeliveries").GetBoolean()));

        var invalid = await client!.GetAsync("/api/admin/dashboard/operations?date=13-08-2026");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Paginates_full_delivery_list()
    {
        if (!enabled) return;

        var page = await client!.GetFromJsonAsync<JsonElement>(
            "/api/admin/dashboard/operations/deliveries?date=2026-08-13&page=1&pageSize=1");
        Assert.Equal(1, page.GetProperty("items").GetArrayLength());
        Assert.Equal(2, page.GetProperty("meta").GetProperty("totalCount").GetInt32());
        Assert.Equal(2, page.GetProperty("meta").GetProperty("totalPages").GetInt32());
    }

    private async Task SeedAsync()
    {
        using var scope = factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DietTimeDbContext>();
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var customerOne = Profile("Ahmed", now);
        var customerTwo = Profile("Fatima", now);
        var brokenCustomer = Profile("No Schedule", now);
        db.CustomerProfiles.AddRange(customerOne, customerTwo, brokenCustomer);

        db.Orders.AddRange(
            Order(customerOne.Id, "ORD-001", new(2026, 8, 13), new(2026, 8, 15), "", 2, [4, 6], new(9, 0), now),
            Order(customerOne.Id, "ORD-002", new(2026, 8, 13), new(2026, 8, 20), "Doha", 4, [4], new(10, 0), now),
            Order(customerTwo.Id, "ORD-003", new(2026, 8, 15), new(2026, 8, 15), "Lusail", 3, [6], new(8, 0), now),
            Order(brokenCustomer.Id, "ORD-004", new(2026, 8, 13), new(2026, 8, 14), "Doha", 1, [6], new(11, 0), now));
        await db.SaveChangesAsync();
    }

    private static CustomerProfile Profile(string name, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), PreferredName = name, PreferredLanguage = "en", OnboardingStatus = "COMPLETED",
        IsActive = true, CreatedAt = now, UpdatedAt = now, RowVersion = 1
    };

    private static Order Order(
        Guid customerId, string number, DateOnly start, DateOnly end, string area,
        int meals, int[] weekdays, TimeOnly slot, DateTimeOffset now)
    {
        var order = new Order
        {
            Id = Guid.NewGuid(), OrderNumber = number, CustomerProfileId = customerId,
            MealPlanTemplateId = Guid.NewGuid(), MealPlanPriceId = Guid.NewGuid(),
            CustomerAddressId = Guid.NewGuid(), DeliveryTimeSlotId = Guid.NewGuid(),
            StartDate = start, EndDate = end, DeliveryDaysPerWeek = weekdays.Length,
            PlanName = "Operations Plan", PlanDurationName = "Test", CurrencyCode = "QAR",
            DeliveryAddressType = CustomerAddressTypes.Home, DeliveryArea = area,
            DeliveryFormattedAddress = area.Length == 0 ? null : $"Building 1, {area}",
            DeliveryTimeSlotName = "Slot", DeliveryStartTime = slot, DeliveryEndTime = slot.AddHours(1),
            Status = OrderStatuses.Confirmed, PaymentStatus = PaymentStatuses.Pending,
            PlacedAt = now, IdempotencyKey = Guid.NewGuid().ToString(), CreatedAt = now, UpdatedAt = now
        };
        order.Meals.Add(new OrderMeal
        {
            Id = Guid.NewGuid(), OrderId = order.Id, MealTypeId = Guid.NewGuid(), MealTypeName = "Meals", Quantity = meals
        });
        foreach (var weekday in weekdays)
            order.DeliveryDays.Add(new OrderDeliveryDay
                { Id = Guid.NewGuid(), OrderId = order.Id, DayOfWeek = weekday });
        return order;
    }

    private sealed class NeverCalledDashboardService : IOperationsDashboardService
    {
        public DateOnly GetBusinessDate() => new(2026, 8, 13);

        public Task<OperationsDashboardResponse> GetAsync(
            DateOnly date, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The service must not be called for invalid input.");

        public Task<DashboardDeliveriesPage> GetDeliveriesAsync(
            DateOnly date, int page, int pageSize, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The service must not be called for invalid input.");
    }
}
