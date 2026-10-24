namespace DietTime.Contracts;

public sealed record MealPackageOptionResponse(
    Guid Id, string Name, int MealCount, int SnackCount, int DisplayOrder, bool IsActive);

public sealed record UpsertMealPackageOptionRequest(
    string Name, int MealCount, int SnackCount, int DisplayOrder, bool IsActive = true);

public sealed record SetActiveStatusRequest(bool IsActive);
public sealed record MealPlanPricingResponse(
    Guid Id, Guid MealPlanId, string MealPlanName, string DurationId, string DurationName,
    Guid PackageOptionId, string PackageName, int MealCount, int SnackCount,
    decimal Price, string CurrencyCode, bool IsActive);

public sealed record UpsertMealPlanPricingRequest(
    Guid MealPlanId, string DurationId, Guid PackageOptionId, decimal Price,
    string CurrencyCode = "QAR", bool IsActive = true);

public sealed record AdminMealTypeLookupResponse(Guid Id, string Code, int DisplayOrder, bool IsActive);
