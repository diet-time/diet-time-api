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
    }
}

public sealed class GuestMenuQueryValidator : AbstractValidator<GuestMenuQuery>
{
    public GuestMenuQueryValidator()
    {
        RuleFor(x => x.Language)
            .NotEmpty()
            .Must(x => x is not null && (x.Equals("en", StringComparison.OrdinalIgnoreCase) || x.Equals("ar", StringComparison.OrdinalIgnoreCase)))
            .WithMessage("Language must be either 'en' or 'ar'.");
        RuleFor(x => x.Date).NotEmpty();
    }
}

public sealed class GuestAllergensQueryValidator : AbstractValidator<GuestAllergensQuery>
{
    public GuestAllergensQueryValidator()
    {
        RuleFor(x => x.Language)
            .NotEmpty()
            .Must(x => x is not null && (x.Equals("en", StringComparison.OrdinalIgnoreCase) || x.Equals("ar", StringComparison.OrdinalIgnoreCase)))
            .WithMessage("Language must be either 'en' or 'ar'.");
    }
}

public sealed class UpsertCustomerProfileRequestValidator : AbstractValidator<UpsertCustomerProfileRequest>
{
    public UpsertCustomerProfileRequestValidator(TimeProvider clock)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        RuleFor(x => x.HeightCm).InclusiveBetween(50m, 300m).When(x => x.HeightCm.HasValue);
        RuleFor(x => x.WeightKg).InclusiveBetween(15m, 500m).When(x => x.WeightKg.HasValue);
        RuleFor(x => x.PreferredLanguage).NotEmpty().MaximumLength(10);
        RuleFor(x => x.OnboardingStatus).NotEmpty().MaximumLength(30);
        RuleFor(x => x.GenderCode).MaximumLength(30);
        RuleFor(x => x.GoalCode).MaximumLength(50);
        RuleFor(x => x.DailyRoutineCode).MaximumLength(50);
        RuleFor(x => x.ActivityLevelCode).MaximumLength(50);
        RuleFor(x => x.DateOfBirth)
            .Must(value => value is null || value <= today)
            .WithMessage("Date of birth cannot be in the future.")
            .Must(value => value is null || value >= today.AddYears(-120))
            .WithMessage("Date of birth cannot represent an age older than 120 years.");

        RuleForEach(x => x.Preferences).ChildRules(preference =>
        {
            preference.RuleFor(x => x.PreferenceCode).NotEmpty().MaximumLength(50);
            preference.RuleFor(x => x.PreferenceType).MaximumLength(30);
            preference.RuleFor(x => x.PreferencePriority).InclusiveBetween(1, 5);
        });
        RuleFor(x => x.Preferences)
            .Must(items => items
                .Select(item => item.PreferenceCode?.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() == items.Count)
            .WithMessage("Duplicate preference codes are not allowed.");

        RuleForEach(x => x.Allergens).ChildRules(allergen =>
        {
            allergen.RuleFor(x => x.AllergenId).NotEmpty();
            allergen.RuleFor(x => x.SeverityCode).MaximumLength(30);
            allergen.RuleFor(x => x.Notes).MaximumLength(500);
        });
        RuleFor(x => x.Allergens)
            .Must(items => items.Select(item => item.AllergenId).Distinct().Count() == items.Count)
            .WithMessage("Duplicate allergen IDs are not allowed.");

        When(
            x => string.Equals(x.OnboardingStatus, "COMPLETED", StringComparison.OrdinalIgnoreCase),
            () =>
            {
                RuleFor(x => x.GenderCode).NotEmpty();
                RuleFor(x => x.DateOfBirth).NotNull();
                RuleFor(x => x.HeightCm).NotNull();
                RuleFor(x => x.WeightKg).NotNull();
                RuleFor(x => x.GoalCode).NotEmpty();
                RuleFor(x => x.DailyRoutineCode).NotEmpty();
                RuleFor(x => x.ActivityLevelCode).NotEmpty();
                RuleFor(x => x.PreferredLanguage).NotEmpty();
            });
    }
}

public sealed class UpdateCustomerPreferredNameRequestValidator
    : AbstractValidator<UpdateCustomerPreferredNameRequest>
{
    public UpdateCustomerPreferredNameRequestValidator()
    {
        RuleFor(x => x.PreferredName).NotEmpty().MinimumLength(2).MaximumLength(150);
    }
}

public sealed class UpdateCustomerPersonalInfoRequestValidator
    : AbstractValidator<UpdateCustomerPersonalInfoRequest>
{
    public UpdateCustomerPersonalInfoRequestValidator(TimeProvider clock)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        RuleFor(x => x.FullName)
            .NotEmpty().WithMessage("Full name is required.")
            .Must(value => value is not null && value.Trim().Length >= 2)
            .WithMessage("Full name must contain at least 2 characters.")
            .Must(value => value is not null && value.Trim().Length <= 150)
            .WithMessage("Full name must not exceed 150 characters.");
        RuleFor(x => x.DateOfBirth)
            .Must(value => value is null || value <= today)
            .WithMessage("Date of birth cannot be in the future.")
            .Must(value => value is null || value >= today.AddYears(-120))
            .WithMessage("Date of birth cannot represent an age older than 120 years.");
        RuleFor(x => x.Gender)
            .NotEmpty()
            .Must(value => value is not null && CustomerGenderCodes.All.Contains(value.Trim()))
            .WithMessage("Gender must be Male or Female.");
    }
}

public sealed class UpsertCustomerAddressRequestValidator
    : AbstractValidator<UpsertCustomerAddressRequest>
{
    public UpsertCustomerAddressRequestValidator()
    {
        RuleFor(x => x.AddressName).MaximumLength(100);
        RuleFor(x => x.AddressType)
            .NotEmpty()
            .Must(value => CustomerAddressTypes.All.Contains(value))
            .WithMessage("Address type must be HOME, APARTMENT, OFFICE, or OTHER.");
        RuleFor(x => x.BuildingNo).Cascade(CascadeMode.Stop).NotEmpty().WithMessage("Building number is required.")
            .Must(value => value != null && value.Trim().Length <= 50 &&
                System.Text.RegularExpressions.Regex.IsMatch(value.Trim(), @"\A[\p{L}\p{M}\p{Nd} /-]+\z") &&
                System.Text.RegularExpressions.Regex.IsMatch(value, @"[\p{L}\p{Nd}]"))
            .WithMessage("Use letters, numbers and valid separators (maximum 50 characters).");
        RuleFor(x => x.StreetNo).Cascade(CascadeMode.Stop).NotEmpty().WithMessage("Street is required.")
            .Must(value => value != null && value.Trim().Length <= 50 &&
                System.Text.RegularExpressions.Regex.IsMatch(value.Trim(), @"\A[\p{L}\p{M}\p{Nd} ./#,\x27’()\-]+\z") &&
                System.Text.RegularExpressions.Regex.IsMatch(value, @"[\p{L}\p{Nd}]"))
            .WithMessage("Use letters, numbers and valid separators (maximum 50 characters).");
        RuleFor(x => x.UnitNumber).Must(value => string.IsNullOrWhiteSpace(value) ||
            value.Trim().Length <= 50 && System.Text.RegularExpressions.Regex.IsMatch(value.Trim(), @"\A[\p{L}\p{M}\p{Nd} /-]+\z") &&
            System.Text.RegularExpressions.Regex.IsMatch(value, @"[\p{L}\p{Nd}]"))
            .WithMessage("Use a valid unit identifier (maximum 50 characters).");
        RuleFor(x => x.ZoneNo).Cascade(CascadeMode.Stop).NotEmpty().WithMessage("Zone number is required.")
            .Must(value => value != null && value.Trim().Length <= 50 &&
                System.Text.RegularExpressions.Regex.IsMatch(value.Trim(), @"\A\p{Nd}+\z") &&
                System.Text.RegularExpressions.Regex.IsMatch(value, @"[\p{L}\p{Nd}]"))
            .WithMessage("Zone number must contain digits only.");
        RuleFor(x => x.Area).Cascade(CascadeMode.Stop).NotEmpty().WithMessage("Area is required.")
            .Must(value => value != null && value.Trim().Length <= 150 &&
                System.Text.RegularExpressions.Regex.IsMatch(value.Trim(), @"\A[\p{L}\p{M} .,\x27’،()&\-]+\z") &&
                System.Text.RegularExpressions.Regex.IsMatch(value, @"\p{L}"))
            .WithMessage("Use English or Arabic letters and name punctuation; no digits.");
        RuleFor(x => x.Directions).MaximumLength(500);
        RuleFor(x => x.Latitude).InclusiveBetween(-90m, 90m).When(x => x.Latitude.HasValue);
        RuleFor(x => x.Longitude).InclusiveBetween(-180m, 180m).When(x => x.Longitude.HasValue);
        RuleFor(x => x.FormattedAddress).MaximumLength(500);
    }
}

public sealed class PlaceOrderRequestValidator : AbstractValidator<PlaceOrderRequest>
{
    public PlaceOrderRequestValidator()
    {
        RuleFor(x => x.CustomerProfileId).NotEmpty();
        RuleFor(x => x.MealPlanTemplateId).NotEmpty();
        RuleFor(x => x.MealPlanPriceId).NotEmpty();
        RuleFor(x => x.FulfillmentMethod).Must(FulfillmentMethods.IsValid);
        RuleFor(x => x.TakeawayBranchId).NotEmpty().When(x => x.FulfillmentMethod == FulfillmentMethods.Takeaway);
        RuleFor(x => x.CustomerAddressId).NotEmpty().When(x => x.FulfillmentMethod == FulfillmentMethods.HomeDelivery);
        RuleFor(x => x.DeliveryTimeSlotId).NotEmpty().When(x => x.FulfillmentMethod == FulfillmentMethods.HomeDelivery);
        RuleFor(x => x.StartDate).NotEqual(default(DateOnly));
        RuleFor(x => x.DeliveryDays).NotNull().NotEmpty().Must(days => days.Count <= 7);
        RuleForEach(x => x.DeliveryDays).InclusiveBetween(1, 7);
        RuleFor(x => x.DeliveryDays)
            .Must(days => days.Distinct().Count() == days.Count)
            .WithMessage("Duplicate delivery days are not allowed.");
        RuleFor(x => x.Meals).Must(meals => meals is null || meals.Count <= 20);
        RuleForEach(x => x.Meals).ChildRules(meal =>
        {
            meal.RuleFor(x => x.MealTypeId).NotEmpty();
            meal.RuleFor(x => x.Quantity).GreaterThan(0);
        });
        RuleFor(x => x.Meals)
            .Must(meals => meals is null || meals.Select(meal => meal.MealTypeId).Distinct().Count() == meals.Count)
            .WithMessage("Duplicate meal types are not allowed.");
        RuleFor(x => x.CouponCode).MaximumLength(100);
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
    public CreatePlanRequestValidator() { RuleFor(x => x.Code).NotEmpty().MaximumLength(50); RuleFor(x => x.PlanType).Must(PlanTypes.Contains); RuleFor(x => x.DurationDays).InclusiveBetween(1, 365); RuleFor(x => x.Translations).NotEmpty(); RuleFor(x => x.CycleWeeks).Must(value => value is null or >= 1 and <= 4); RuleFor(x => x.WeekNumber).Must(value => value is null or >= 1 and <= 4); }
}

public sealed class UpsertMealPlanTemplateDayRequestValidator : AbstractValidator<UpsertMealPlanTemplateDayRequest>
{
    public UpsertMealPlanTemplateDayRequestValidator()
    {
        RuleFor(x => x.MenuWeekday).NotNull().IsInEnum();
        RuleFor(x => x.DisplayOrder).GreaterThan(0);
        RuleFor(x => x.WeekNumber).InclusiveBetween(1, 4);
    }
}

public sealed class UpsertMealPlanPricePackageRequestValidator
    : AbstractValidator<UpsertMealPlanPricePackageRequest>
{
    public UpsertMealPlanPricePackageRequestValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty()
            .MaximumLength(50)
            .Matches("^[\\p{L}\\p{N}_ -]+$")
            .WithMessage("Code may contain letters, numbers, spaces, underscores, and hyphens.");
        RuleFor(x => x.NameEn).NotEmpty().MaximumLength(100);
        RuleFor(x => x.NameAr).NotEmpty().MaximumLength(100);
        RuleFor(x => x.DurationDays).GreaterThan(0);
        RuleFor(x => x.DisplayOrder).GreaterThanOrEqualTo(0);
    }
}
