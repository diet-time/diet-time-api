using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;

namespace DietTime.UnitTests;

public sealed class WeeklyMenuTests
{
    private static readonly DayOfWeek[] DeliveryDays =
    [
        DayOfWeek.Saturday,
        DayOfWeek.Sunday,
        DayOfWeek.Monday,
        DayOfWeek.Tuesday,
        DayOfWeek.Wednesday,
        DayOfWeek.Thursday
    ];

    [Fact]
    public async Task Tuesday_joining_date_returns_Wednesday()
    {
        var service = Service();
        var result = await service.GetNextDeliveryDateAsync(new(2026, 7, 14), DeliveryDays, [], default);
        Assert.Equal(new DateOnly(2026, 7, 15), result);
    }

    [Fact]
    public async Task Wednesday_joining_date_returns_Thursday()
    {
        var service = Service();
        var result = await service.GetNextDeliveryDateAsync(new(2026, 7, 15), DeliveryDays, [], default);
        Assert.Equal(new DateOnly(2026, 7, 16), result);
    }

    [Fact]
    public async Task Thursday_joining_date_skips_Friday_and_returns_Saturday()
    {
        var service = Service();
        var result = await service.GetNextDeliveryDateAsync(new(2026, 7, 16), DeliveryDays, [], default);
        Assert.Equal(new DateOnly(2026, 7, 18), result);
    }

    [Fact]
    public async Task Holiday_is_skipped()
    {
        var service = Service();
        var result = await service.GetNextDeliveryDateAsync(new(2026, 7, 14), DeliveryDays, [new(2026, 7, 15)], default);
        Assert.Equal(new DateOnly(2026, 7, 16), result);
    }

    [Fact]
    public async Task Kitchen_closure_is_skipped()
    {
        var service = Service(new HashSet<DateOnly> { new(2026, 7, 16) });
        var result = await service.GetNextDeliveryDateAsync(new(2026, 7, 15), DeliveryDays, [], default);
        Assert.Equal(new DateOnly(2026, 7, 18), result);
    }

    [Fact]
    public async Task Delivery_date_loads_matching_weekday_menu()
    {
        var expected = Day(MenuWeekday.Wednesday);
        var reader = new StubMenuReader(expected);
        var service = new DeliverySchedulingService(new StubCalendar([]), reader);

        var result = await service.GetMenuForDeliveryDateAsync(Guid.NewGuid(), new(2026, 7, 15), default);

        Assert.Equal(MenuWeekday.Wednesday, reader.RequestedWeekday);
        Assert.Same(expected, result);
    }

    [Fact]
    public async Task Missing_weekday_menu_returns_clear_error()
    {
        var service = new DeliverySchedulingService(new StubCalendar([]), new StubMenuReader(null));
        var error = await Assert.ThrowsAsync<TemplateDayException>(() =>
            service.GetMenuForDeliveryDateAsync(Guid.NewGuid(), new(2026, 7, 15), default));
        Assert.Equal("MENU_NOT_CONFIGURED_FOR_WEEKDAY", error.Code);
    }

    [Fact]
    public void Weekday_codes_are_stable_uppercase_strings()
    {
        Assert.Equal("WEDNESDAY", MenuWeekday.Wednesday.Code());
    }

    private static DeliverySchedulingService Service(HashSet<DateOnly>? closures = null) =>
        new(new StubCalendar(closures ?? []), new StubMenuReader(null));

    private static MealPlanTemplateDayDetailResponse Day(MenuWeekday weekday) =>
        new(Guid.NewGuid(), Guid.NewGuid(), weekday, 5, true, 0, []);

    private sealed class StubCalendar(HashSet<DateOnly> closures) : IOperationalCalendarService
    {
        public Task<bool> IsOperationalDateAsync(DateOnly date, CancellationToken cancellationToken) =>
            Task.FromResult(!closures.Contains(date));
    }

    private sealed class StubMenuReader(MealPlanTemplateDayDetailResponse? result) : ITemplateMenuReader
    {
        public MenuWeekday? RequestedWeekday { get; private set; }
        public Task<MealPlanTemplateDayDetailResponse?> GetTemplateDayByWeekdayAsync(Guid templateId, MenuWeekday weekday, CancellationToken cancellationToken)
        {
            RequestedWeekday = weekday;
            return Task.FromResult(result);
        }
    }
}
