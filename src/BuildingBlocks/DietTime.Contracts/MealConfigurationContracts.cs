using DietTime.Domain;
using System.Text.Json.Serialization;
namespace DietTime.Contracts;

public sealed record MealPackageOptionResponse(
    Guid Id, string Name, int MealCount, int SnackCount, int DisplayOrder, bool IsActive);

public sealed record UpsertMealPackageOptionRequest(
    string Name, int MealCount, int SnackCount, int DisplayOrder, bool IsActive = true);

public sealed record SetActiveStatusRequest(bool IsActive);
public sealed record MealPlanPricingResponse(
    Guid Id, Guid MealPlanId, string MealPlanName, string DurationId, string DurationName,
    Guid? PackageOptionId, string PackageName, int MealCount, int SnackCount,
    decimal Price, string CurrencyCode, bool IsActive, int ServiceDays = 0, bool IsAvailable = false,
    decimal? DiscountedPrice = null)
{
    public decimal RegularPrice => Price;
    public decimal EffectivePrice => PackagePriceRules.Calculate(RegularPrice, DiscountedPrice).EffectivePrice;
    public decimal DiscountAmount => PackagePriceRules.Calculate(RegularPrice, DiscountedPrice).DiscountAmount;
    public decimal DiscountPercentage => PackagePriceRules.Calculate(RegularPrice, DiscountedPrice).DiscountPercentage;
    public bool HasDiscount => DiscountedPrice.HasValue;
}


public sealed record UpsertMealPlanPricingRequest(
    Guid MealPlanId, string DurationId, Guid PackageOptionId, decimal Price,
    string CurrencyCode = "QAR", bool IsActive = true)
{

    private decimal? discountedPrice;
    public decimal? DiscountedPrice { get => discountedPrice; init { discountedPrice = value; DiscountedPriceSpecified = true; } }
    [JsonIgnore] public bool DiscountedPriceSpecified { get; private init; }
}


public sealed record AdminMealTypeLookupResponse(Guid Id, string Code, int DisplayOrder, bool IsActive);
