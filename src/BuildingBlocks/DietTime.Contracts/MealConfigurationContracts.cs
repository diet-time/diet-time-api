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
    Guid Id, Guid MealPlanId, string MealPlanName, Guid DurationId, string DurationName,
    Guid PackageOptionId, string PackageName, int MealCount, int SnackCount,
    decimal Price, string CurrencyCode, bool IsActive);

public sealed record UpsertMealPlanPricingRequest(
    Guid MealPlanId, Guid DurationId, Guid PackageOptionId, decimal Price,
    string CurrencyCode = "QAR", bool IsActive = true);

public sealed record WeeklyMenuItemRequest(Guid MenuItemId, bool IsDefault, int DisplayOrder);
public sealed record WeeklyMenuMealTypeRequest(Guid MealTypeId, IReadOnlyList<WeeklyMenuItemRequest> Items);
public sealed record UpdateWeeklyMenuDayRequest(bool IsActive, IReadOnlyList<WeeklyMenuMealTypeRequest> MealTypes);
public sealed record WeeklyMenuItemResponse(Guid MenuItemId, string Name, bool IsDefault, int DisplayOrder);
public sealed record WeeklyMenuMealTypeResponse(Guid MealTypeId, string Code, IReadOnlyList<WeeklyMenuItemResponse> Items);
public sealed record WeeklyMenuDayResponse(int DayOfWeek, string DayName, bool IsActive, IReadOnlyList<WeeklyMenuMealTypeResponse> MealTypes);
public sealed record WeeklyMenuResponse(Guid MealPlanId, string MealPlanName, IReadOnlyList<WeeklyMenuDayResponse> Days);
public sealed record AdminMealTypeLookupResponse(Guid Id, string Code, int DisplayOrder, bool IsActive);
