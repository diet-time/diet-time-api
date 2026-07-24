using DietTime.Contracts;
using DietTime.Domain;
using FluentValidation;

namespace DietTime.Application;

public sealed class MealSelectionRequestValidator : AbstractValidator<MealSelectionRequest>
{
    public MealSelectionRequestValidator()
    {
        RuleFor(x => x.PlanId).NotEmpty(); RuleFor(x => x.TemplateDayId).NotEmpty(); RuleFor(x => x.Selections).NotNull().Must(x => x.Count <= 50);
        RuleForEach(x => x.Selections).ChildRules(item => { item.RuleFor(x => x.SlotId).NotEmpty(); item.RuleFor(x => x.SlotOptionId).NotEmpty(); item.RuleFor(x => x.MealItemId).NotEmpty(); });
    }
}

public sealed class GuestHomeQueryValidator : AbstractValidator<GuestHomeQuery>
{
    private static readonly string[] MealTimeCodes = ["ALL", "BREAKFAST", "LUNCH", "DINNER", "SNACK"];

    public GuestHomeQueryValidator()
    {
        RuleFor(x => x.Language)
            .NotEmpty()
            .Must(x => x is not null && (x.Equals("en", StringComparison.OrdinalIgnoreCase) || x.Equals("ar", StringComparison.OrdinalIgnoreCase)))
            .WithMessage("Language must be either 'en' or 'ar'.");
        RuleFor(x => x.PlanCode)
            .MaximumLength(100)
            .Matches("^[a-zA-Z0-9_-]+$")
            .When(x => !string.IsNullOrWhiteSpace(x.PlanCode));
        RuleFor(x => x.MealTimeCode)
            .NotEmpty()
            .Must(x => x is not null && MealTimeCodes.Contains(x.Trim().ToUpperInvariant()))
            .WithMessage("MealTimeCode must be ALL, BREAKFAST, LUNCH, DINNER, or SNACK.");
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed class UpsertAllergenRequestValidator : AbstractValidator<UpsertAllergenRequest>
{
    public UpsertAllergenRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(50).Matches("^[a-zA-Z0-9_-]+$");
        RuleFor(x => x.NameEn).NotEmpty().MaximumLength(100);
        RuleFor(x => x.NameAr).NotEmpty().MaximumLength(100);
    }
}

public sealed class UpsertIngredientRequestValidator : AbstractValidator<UpsertIngredientRequest>
{
    public UpsertIngredientRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(50).Matches("^[a-zA-Z0-9_-]+$");
        RuleFor(x => x.NameEn).NotEmpty().MaximumLength(150);
        RuleFor(x => x.NameAr).NotEmpty().MaximumLength(150);
    }
}

public sealed class UpsertMealCategoryRequestValidator : AbstractValidator<UpsertMealCategoryRequest>
{
    public UpsertMealCategoryRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(50).Matches("^[a-zA-Z0-9_-]+$");
        RuleFor(x => x.NameEn).NotEmpty().MaximumLength(100);
        RuleFor(x => x.NameAr).NotEmpty().MaximumLength(100);
        RuleFor(x => x.DisplayOrder).GreaterThanOrEqualTo(0);
    }
}

public sealed class UpsertMealRequestValidator : AbstractValidator<UpsertMealRequest>
{
    public UpsertMealRequestValidator()
    {
        RuleFor(x => x.Sku).NotEmpty().MaximumLength(50).Matches("^[A-Z0-9_-]+$"); RuleFor(x => x.CategoryId).NotEmpty();
        RuleFor(x => x.Status).Must(status => status is null || MealStatuses.IsValid(status))
            .WithMessage("Status must be DRAFT, ACTIVE, INACTIVE, or ARCHIVED.");
        RuleFor(x => x.SpiceLevel).InclusiveBetween((short)0, (short)5).When(x => x.SpiceLevel.HasValue);
        RuleFor(x => x.PreparationTimeMinutes).GreaterThanOrEqualTo(0).When(x => x.PreparationTimeMinutes.HasValue);
        RuleFor(x => x.Translations).NotEmpty().Must(x => x.Any(t => t.LanguageCode.Equals("en", StringComparison.OrdinalIgnoreCase))).WithMessage("An English translation is required.");
        RuleForEach(x => x.Translations).ChildRules(t => { t.RuleFor(x => x.LanguageCode).Must(v => v is "en" or "ar"); t.RuleFor(x => x.Name).NotEmpty().MaximumLength(200); });
        RuleForEach(x => x.Ingredients).ChildRules(i => { i.RuleFor(x => x.IngredientId).NotEmpty(); i.RuleFor(x => x.Quantity).GreaterThanOrEqualTo(0).When(x => x.Quantity.HasValue); i.RuleFor(x => x.DisplayOrder).GreaterThanOrEqualTo(0); });
        RuleForEach(x => x.Allergens).ChildRules(a => { a.RuleFor(x => x.AllergenId).NotEmpty(); a.RuleFor(x => x.Level).Must(v => v is "CONTAINS" or "MAY_CONTAIN" or "TRACES"); });
        RuleForEach(x => x.Prices).ChildRules(p => { p.RuleFor(x => x.Amount).GreaterThanOrEqualTo(0); p.RuleFor(x => x.CurrencyCode).Length(3); p.RuleFor(x => x).Must(v => v.EffectiveUntil is null || v.EffectiveUntil > v.EffectiveFrom); });
        RuleFor(x => x).Must(x => x.AvailableUntil is null || x.AvailableFrom is null || x.AvailableUntil > x.AvailableFrom).WithMessage("availableUntil must be after availableFrom.");
    }
}

public sealed class CreatePlanRequestValidator : AbstractValidator<CreatePlanRequest>
{
    private static readonly string[] PlanTypes = ["STANDARD", "WEIGHT_LOSS", "WEIGHT_GAIN", "KETO", "DIABETIC", "VEGETARIAN", "VEGAN", "HIGH_PROTEIN", "LOW_CARB", "BALANCED", "CUSTOM"];
    public CreatePlanRequestValidator() { RuleFor(x => x.Code).NotEmpty().MaximumLength(50); RuleFor(x => x.PlanType).Must(PlanTypes.Contains); RuleFor(x => x.DurationDays).InclusiveBetween(1, 365); RuleFor(x => x.Translations).NotEmpty(); }
}

public sealed class UpsertMealPlanTemplateDayRequestValidator : AbstractValidator<UpsertMealPlanTemplateDayRequest>
{
    public UpsertMealPlanTemplateDayRequestValidator()
    {
        RuleFor(x => x.MenuWeekday).NotNull().IsInEnum();
        RuleFor(x => x.DisplayOrder).GreaterThan(0);
    }
}
