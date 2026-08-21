using DietTime.Contracts;

namespace DietTime.Application;

public sealed class MealConfigurationException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public interface IMealPackageService
{
    Task<IReadOnlyList<MealPackageOptionResponse>> GetAsync(bool activeOnly, CancellationToken cancellationToken);
    Task<MealPackageOptionResponse?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<Guid> CreateAsync(UpsertMealPackageOptionRequest request, Guid? userId, CancellationToken cancellationToken);
    Task UpdateAsync(Guid id, UpsertMealPackageOptionRequest request, Guid? userId, CancellationToken cancellationToken);
    Task SetStatusAsync(Guid id, bool isActive, Guid? userId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PackageMealTypeResponse>> GetMealTypesAsync(Guid packageOptionId, CancellationToken cancellationToken);
    Task UpdateMealTypesAsync(Guid packageOptionId, UpdatePackageMealTypesRequest request, CancellationToken cancellationToken);
}

public interface IMealPlanPricingService
{
    Task<IReadOnlyList<MealPlanPricingResponse>> GetAsync(Guid? mealPlanId, Guid? durationId, Guid? packageOptionId, bool activeOnly, CancellationToken cancellationToken);
    Task<Guid> CreateAsync(UpsertMealPlanPricingRequest request, Guid? userId, CancellationToken cancellationToken);
    Task UpdateAsync(Guid id, UpsertMealPlanPricingRequest request, Guid? userId, CancellationToken cancellationToken);
}

public interface IWeeklyMenuService
{
    Task<WeeklyMenuResponse> GetAsync(Guid mealPlanId, CancellationToken cancellationToken);
    Task<WeeklyMenuDayResponse> GetDayAsync(Guid mealPlanId, int dayOfWeek, CancellationToken cancellationToken);
    Task UpdateDayAsync(Guid mealPlanId, int dayOfWeek, UpdateWeeklyMenuDayRequest request, Guid? userId, CancellationToken cancellationToken);
}
