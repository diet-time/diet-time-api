namespace DietTime.Contracts;

public sealed record PackageMealTypeResponse(
    Guid MealTypeId, string Code, bool IsRequired, int MaxQuantity, int DisplayOrder = 0,
    bool Selected = true);

public sealed record MealPackageOptionResponse(
    Guid Id, string Name, int MealCount, int SnackCount, int DisplayOrder, bool IsActive,
    IReadOnlyList<PackageMealTypeResponse> MealTypes);

public sealed record UpsertMealPackageOptionRequest(
    string Name, int MealCount, int SnackCount, int DisplayOrder, bool IsActive = true);

public sealed record SetActiveStatusRequest(bool IsActive);
public sealed record PackageMealTypeRequest(Guid MealTypeId, bool IsRequired, int MaxQuantity, int DisplayOrder);
public sealed record UpdatePackageMealTypesRequest(IReadOnlyList<PackageMealTypeRequest> MealTypes);

public sealed record MealPlanPricingResponse(
    Guid Id, Guid MealPlanId, string MealPlanName, string DurationId, string DurationName,
    Guid PackageOptionId, string PackageName, int MealCount, int SnackCount,
    decimal Price, string CurrencyCode, bool IsActive);

public sealed record UpsertMealPlanPricingRequest(
    Guid MealPlanId, string DurationId, Guid PackageOptionId, decimal Price,
    string CurrencyCode = "QAR", bool IsActive = true);

public sealed record AdminMealTypeLookupResponse(Guid Id, string Code, int DisplayOrder, bool IsActive);
