namespace DietTime.Domain;

public sealed record PackagePriceBreakdown(decimal RegularPrice, decimal? DiscountedPrice,
    decimal EffectivePrice, decimal DiscountAmount, decimal DiscountPercentage);

public static class PackagePriceRules
{
    public const decimal MaximumAmount = 9999999999.99m;
    public static bool IsMoney(decimal value) => value >= 0 && value <= MaximumAmount && decimal.Round(value, 2) == value;
    public static bool IsValid(decimal regular, decimal? discounted) => IsMoney(regular) &&
        (!discounted.HasValue || IsMoney(discounted.Value) && discounted.Value < regular);

    public static PackagePriceBreakdown Calculate(decimal regular, decimal? discounted)
    {
        if (!IsValid(regular, discounted)) throw new ArgumentException("Invalid package price or discount.");
        var effective = discounted ?? regular;
        var saving = regular - effective;
        return new(regular, discounted, effective, saving,
            regular == 0 ? 0 : decimal.Round(saving / regular * 100, 0, MidpointRounding.AwayFromZero));
    }
}
