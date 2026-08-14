using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using DietTime.Meal.Api.Controllers;
using DietTime.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed class DeliveryPreparationApiTests : IAsyncLifetime
{
    private readonly bool enabled = Environment.GetEnvironmentVariable("RUN_INTEGRATION_TESTS") == "true";
    private PostgreSqlContainer? postgres;
    private ApiFactory? factory;
    private HttpClient? client;

    public async Task InitializeAsync()
    {
        if (!enabled) return;
        postgres = new PostgreSqlBuilder().WithImage("postgres:16-alpine")
            .WithDatabase("diettime_preparation_test").WithUsername("postgres").WithPassword("postgres").Build();
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
    public async Task Invalid_date_is_rejected_before_querying_the_service()
    {
        var controller = new DeliveryPreparationController(
            new NeverCalledCalendarService(),
            new NeverCalledReportGenerator(),
            NullLogger<DeliveryPreparationController>.Instance);

        var result = await controller.GetPreparationSummary("15-08-2026", default);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.IsType<ValidationProblemDetails>(problem.Value);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains("date", controller.ModelState.Keys);
    }

    [Fact]
    public async Task Invalid_report_date_is_rejected_before_querying_the_service()
    {
        var controller = new DeliveryPreparationController(
            new NeverCalledCalendarService(),
            new NeverCalledReportGenerator(),
            NullLogger<DeliveryPreparationController>.Instance);

        var result = await controller.GetPreparationReport("15-08-2026", default);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.IsType<ValidationProblemDetails>(problem.Value);
        Assert.False(controller.ModelState.IsValid);
    }

    [Fact]
    public async Task Json_and_pdf_flows_use_the_same_preparation_service_result()
    {
        var summary = new DeliveryPreparationSummaryResponse(
            new(2026, 8, 16), "Scheduled", 1, 1, 2,
            [new(Guid.NewGuid(), "Breakfast", 2,
                [new(Guid.NewGuid(), "Oatmeal", 2)])],
            [new(Guid.NewGuid(), "Balanced Living", 1)]);
        var calendar = new RecordingCalendarService(summary);
        var generator = new RecordingReportGenerator();
        var controller = new DeliveryPreparationController(
            calendar, generator, NullLogger<DeliveryPreparationController>.Instance);

        await controller.GetPreparationSummary("2026-08-16", default);
        var result = await controller.GetPreparationReport("2026-08-16", default);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.Equal("Kitchen-Preparation-2026-08-16.pdf", file.FileDownloadName);
        Assert.Equal([1, 2, 3, 4], file.FileContents);
        Assert.Equal(2, calendar.CallCount);
        Assert.All(calendar.RequestedDates, date => Assert.Equal(summary.Date, date));
        Assert.Same(summary, generator.Summary);
    }

    [Fact]
    public void Report_endpoint_uses_the_delivery_calendar_admin_authorization()
    {
        var authorization = Assert.Single(typeof(DeliveryPreparationController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>());

        Assert.Contains("Admin", authorization.Roles);
        Assert.Contains("Operations", authorization.Roles);
    }

    [Fact]
    public async Task Report_response_is_a_downloadable_pdf_for_scheduled_and_empty_days()
    {
        if (!enabled) return;

        foreach (var date in new[] { "2026-08-15", "2026-08-14" })
        {
            var response = await client!.GetAsync(
                $"/api/admin/delivery-calendar/{date}/preparation-report");
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync();

            Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal($"Kitchen-Preparation-{date}.pdf",
                response.Content.Headers.ContentDisposition?.FileNameStar);
            Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
        }
    }

    [Fact]
    public async Task Aggregates_default_menu_assignments_and_excludes_invalid_schedules()
    {
        if (!enabled) return;

        var response = await client!.GetAsync(
            "/api/admin/delivery-calendar/2026-08-15/preparation-summary");
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<JsonElement>();
        var body = envelope.GetProperty("data");

        Assert.Equal("Scheduled", body.GetProperty("status").GetString());
        Assert.Equal(3, body.GetProperty("orderCount").GetInt32());
        Assert.Equal(2, body.GetProperty("customerCount").GetInt32());
        Assert.Equal(10, body.GetProperty("mealItemCount").GetInt32());
        var breakfast = body.GetProperty("mealTypes")[0];
        Assert.Equal("Breakfast", breakfast.GetProperty("mealTypeName").GetString());
        Assert.Equal(5, breakfast.GetProperty("quantity").GetInt32());
        Assert.Equal("Chicken Wrap", breakfast.GetProperty("items")[0].GetProperty("menuItemName").GetString());
        Assert.Equal(3, breakfast.GetProperty("items")[0].GetProperty("quantity").GetInt32());
        Assert.Equal(2, body.GetProperty("planBreakdown").GetArrayLength());
        Assert.Equal(2, body.GetProperty("planBreakdown")[0].GetProperty("orderCount").GetInt32());
    }

    [Fact]
    public async Task Returns_empty_state_and_rejects_invalid_date()
    {
        if (!enabled) return;

        var emptyEnvelope = await client!.GetFromJsonAsync<JsonElement>(
            "/api/admin/delivery-calendar/2026-08-14/preparation-summary");
        var empty = emptyEnvelope.GetProperty("data");
        Assert.Equal("NoDeliveries", empty.GetProperty("status").GetString());
        Assert.Equal(0, empty.GetProperty("mealItemCount").GetInt32());
        Assert.Empty(empty.GetProperty("mealTypes").EnumerateArray());

        var invalid = await client!.GetAsync(
            "/api/admin/delivery-calendar/15-08-2026/preparation-summary");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    private async Task SeedAsync()
    {
        using var scope = factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DietTimeDbContext>();
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var customerA = Profile(now);
        var customerB = Profile(now);
        db.CustomerProfiles.AddRange(customerA, customerB);

        var category = new MealCategory
        {
            Id = Guid.NewGuid(), Code = "MAIN", IsActive = true,
            CreatedAt = now, UpdatedAt = now, RowVersion = 1
        };
        db.MealCategories.Add(category);
        var breakfast = MealType("BREAKFAST", "Breakfast", 1, now);
        var lunch = MealType("LUNCH", "Lunch", 2, now);
        var wrap = Meal("WRAP", "Chicken Wrap", category, now);
        var croissant = Meal("CROISSANT", "Egg Croissant", category, now);
        var chicken = Meal("CHICKEN", "Grilled Chicken", category, now);
        db.MealTypes.AddRange(breakfast, lunch);
        db.MealItems.AddRange(wrap, croissant, chicken);

        var planA = Plan("EVERYDAY", breakfast, wrap, lunch, chicken, now);
        var planB = Plan("BALANCED", breakfast, croissant, lunch, chicken, now);
        // Legacy plan data may have no default marker. Preparation should still
        // resolve the first configured option instead of returning an empty day.
        foreach (var option in planB.Days.SelectMany(day => day.Slots).SelectMany(slot => slot.Options))
            option.IsDefault = false;
        db.MealPlanTemplates.AddRange(planA, planB);
        db.Orders.AddRange(
            Order(customerA.Id, planA, "ORD-001", OrderStatuses.Confirmed, [6], 1, now),
            Order(customerA.Id, planA, "ORD-002", OrderStatuses.Confirmed, [6], 2, now),
            Order(customerB.Id, planB, "ORD-003", OrderStatuses.Confirmed, [6], 2, now),
            Order(customerB.Id, planA, "ORD-CANCELLED", "CANCELLED", [6], 9, now),
            Order(customerB.Id, planA, "ORD-SUNDAY", OrderStatuses.Confirmed, [7], 9, now));
        await db.SaveChangesAsync();
    }

    private static CustomerProfile Profile(DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), PreferredLanguage = "en", OnboardingStatus = "COMPLETED",
        IsActive = true, CreatedAt = now, UpdatedAt = now, RowVersion = 1
    };

    private static MealType MealType(string code, string name, int order, DateTimeOffset now)
    {
        var type = new MealType
        {
            Id = Guid.NewGuid(), Code = code, DisplayOrder = order, IsActive = true,
            CreatedAt = now, UpdatedAt = now
        };
        type.Translations.Add(new MealTypeTranslation
        {
            Id = Guid.NewGuid(), LanguageCode = "en", Name = name, CreatedAt = now, UpdatedAt = now
        });
        return type;
    }

    private static MealItem Meal(
        string sku, string name, MealCategory category, DateTimeOffset now)
    {
        var item = new MealItem
        {
            Id = Guid.NewGuid(), VersionGroupId = Guid.NewGuid(), Sku = sku, Category = category,
            Status = "ACTIVE", IsLatest = true, IsAvailable = true,
            CreatedAt = now, UpdatedAt = now, RowVersion = 1
        };
        item.Translations.Add(new MealItemTranslation
        {
            Id = Guid.NewGuid(), LanguageCode = "en", Name = name, CreatedAt = now, UpdatedAt = now
        });
        return item;
    }

    private static MealPlanTemplate Plan(
        string code, MealType breakfast, MealItem breakfastItem,
        MealType lunch, MealItem lunchItem, DateTimeOffset now)
    {
        var plan = new MealPlanTemplate
        {
            Id = Guid.NewGuid(), VersionGroupId = Guid.NewGuid(), Code = code,
            IsLatest = true, IsActive = true, IsPublished = true,
            CreatedAt = now, UpdatedAt = now, RowVersion = 1
        };
        var day = new MealPlanTemplateDay
        {
            Id = Guid.NewGuid(), Plan = plan, MenuWeekday = MenuWeekday.Saturday,
            DisplayOrder = 1, IsActive = true, CreatedAt = now, UpdatedAt = now
        };
        day.Slots.Add(Slot(day, breakfast, breakfastItem, 1, now));
        day.Slots.Add(Slot(day, lunch, lunchItem, 2, now));
        plan.Days.Add(day);
        return plan;
    }

    private static MealPlanTemplateSlot Slot(
        MealPlanTemplateDay day, MealType type, MealItem item, int order, DateTimeOffset now)
    {
        var slot = new MealPlanTemplateSlot
        {
            Id = Guid.NewGuid(), Day = day, MealType = type, DisplayOrder = order,
            MinimumSelection = 1, MaximumSelection = 1, IsRequired = true, IsActive = true,
            CreatedAt = now, UpdatedAt = now, RowVersion = 1
        };
        slot.Options.Add(new MealPlanSlotOption
        {
            Id = Guid.NewGuid(), Slot = slot, MealItem = item, IsDefault = true,
            IsAvailable = true, DisplayOrder = 1, CreatedAt = now, UpdatedAt = now
        });
        return slot;
    }

    private static Order Order(
        Guid customerId, MealPlanTemplate plan, string number, string status,
        int[] weekdays, int breakfastQuantity, DateTimeOffset now)
    {
        var breakfast = plan.Days.Single().Slots.Single(slot => slot.MealType.Code == "BREAKFAST").MealType;
        var lunch = plan.Days.Single().Slots.Single(slot => slot.MealType.Code == "LUNCH").MealType;
        var order = new Order
        {
            Id = Guid.NewGuid(), OrderNumber = number, CustomerProfileId = customerId,
            MealPlanTemplateId = plan.Id, MealPlanPriceId = Guid.NewGuid(),
            CustomerAddressId = Guid.NewGuid(), DeliveryTimeSlotId = Guid.NewGuid(),
            StartDate = new(2026, 8, 15), EndDate = new(2026, 8, 30),
            DeliveryDaysPerWeek = weekdays.Length, PlanName = plan.Code,
            PlanDurationName = "Test", CurrencyCode = "QAR", DeliveryAddressType = "HOME",
            DeliveryArea = "Doha", DeliveryTimeSlotName = "Morning",
            DeliveryStartTime = new(9, 0), DeliveryEndTime = new(11, 0),
            Status = status, PaymentStatus = PaymentStatuses.Pending,
            PlacedAt = now, IdempotencyKey = Guid.NewGuid().ToString(), CreatedAt = now, UpdatedAt = now
        };
        order.Meals.Add(new OrderMeal
        {
            Id = Guid.NewGuid(), OrderId = order.Id, MealTypeId = breakfast.Id,
            MealTypeName = "Breakfast", Quantity = breakfastQuantity
        });
        order.Meals.Add(new OrderMeal
        {
            Id = Guid.NewGuid(), OrderId = order.Id, MealTypeId = lunch.Id,
            MealTypeName = "Lunch", Quantity = breakfastQuantity
        });
        foreach (var weekday in weekdays)
            order.DeliveryDays.Add(new OrderDeliveryDay
                { Id = Guid.NewGuid(), OrderId = order.Id, DayOfWeek = weekday });
        return order;
    }

    private sealed class NeverCalledCalendarService : IAdminDeliveryCalendarService
    {
        public Task<AdminDeliveryCalendarResponse> GetMonthAsync(
            DateOnly startDate, DateOnly endDate, Guid? planId, string? orderStatus,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Storage must not be queried for an invalid date.");

        public Task<DeliveryPreparationSummaryResponse> GetPreparationSummaryAsync(
            DateOnly date, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Storage must not be queried for an invalid date.");
    }

    private sealed class NeverCalledReportGenerator : IKitchenPreparationReportGenerator
    {
        public Task<byte[]> GenerateAsync(
            DeliveryPreparationSummaryResponse summary,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Report generation must not run for an invalid date.");
    }

    private sealed class RecordingCalendarService(DeliveryPreparationSummaryResponse summary)
        : IAdminDeliveryCalendarService
    {
        public int CallCount { get; private set; }
        public List<DateOnly> RequestedDates { get; } = [];

        public Task<AdminDeliveryCalendarResponse> GetMonthAsync(
            DateOnly startDate, DateOnly endDate, Guid? planId, string? orderStatus,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DeliveryPreparationSummaryResponse> GetPreparationSummaryAsync(
            DateOnly date, CancellationToken cancellationToken)
        {
            CallCount++;
            RequestedDates.Add(date);
            return Task.FromResult(summary);
        }
    }

    private sealed class RecordingReportGenerator : IKitchenPreparationReportGenerator
    {
        public DeliveryPreparationSummaryResponse? Summary { get; private set; }

        public Task<byte[]> GenerateAsync(
            DeliveryPreparationSummaryResponse summary,
            CancellationToken cancellationToken)
        {
            Summary = summary;
            return Task.FromResult<byte[]>([1, 2, 3, 4]);
        }
    }
}
