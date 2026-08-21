namespace DietTime.Domain;

public enum MenuWeekday
{
    Saturday,
    Sunday,
    Monday,
    Tuesday,
    Wednesday,
    Thursday,
    Friday
}

public static class MenuWeekdayExtensions
{
    public static MenuWeekday FromDate(DateOnly date) => date.DayOfWeek switch
    {
        DayOfWeek.Saturday => MenuWeekday.Saturday,
        DayOfWeek.Sunday => MenuWeekday.Sunday,
        DayOfWeek.Monday => MenuWeekday.Monday,
        DayOfWeek.Tuesday => MenuWeekday.Tuesday,
        DayOfWeek.Wednesday => MenuWeekday.Wednesday,
        DayOfWeek.Thursday => MenuWeekday.Thursday,
        DayOfWeek.Friday => MenuWeekday.Friday,
        _ => throw new ArgumentOutOfRangeException(nameof(date))
    };

    public static string Code(this MenuWeekday weekday) => weekday.ToString().ToUpperInvariant();
}

public abstract class Entity { public Guid Id { get; set; } }
public abstract class Translation : Entity { public string LanguageCode { get; set; } = "en"; public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } }

public sealed class MealCategory : Entity { public string Code { get; set; } = ""; public int DisplayOrder { get; set; } public bool IsActive { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public Guid? CreatedBy { get; set; } public Guid? UpdatedBy { get; set; } public long RowVersion { get; set; } public ICollection<MealCategoryTranslation> Translations { get; set; } = []; }
public sealed class MealCategoryTranslation : Translation { public Guid MealCategoryId { get; set; } public MealCategory Category { get; set; } = null!; public string Name { get; set; } = ""; public string? Description { get; set; } }

public sealed class MealItem : Entity
{
    public Guid VersionGroupId { get; set; }
    public int VersionNumber { get; set; } = 1;
    public bool IsLatest { get; set; } = true;
    public Guid? SupersedesId { get; set; }
    public string Sku { get; set; } = ""; public Guid CategoryId { get; set; }
    public MealCategory Category { get; set; } = null!;
    public int? PreparationTimeMinutes { get; set; }
    public decimal? DefaultServingQuantity { get; set; }
    public string? DefaultServingUnit { get; set; }
    public bool IsVegetarian { get; set; }
    public bool IsVegan { get; set; }
    public bool IsGlutenFree { get; set; }
    public bool IsDairyFree { get; set; }
    public bool IsNutFree { get; set; }
    public bool IsSpicy { get; set; }
    public short SpiceLevel { get; set; }
    public string Status { get; set; } = "DRAFT"; public bool IsAvailable { get; set; }
    public DateTimeOffset? AvailableFrom { get; set; }
    public DateTimeOffset? AvailableUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public long RowVersion { get; set; }
    public ICollection<MealItemTranslation> Translations { get; set; } = []; public MealNutrition? Nutrition { get; set; }
    public ICollection<MealItemIngredient> Ingredients { get; set; } = []; public ICollection<MealItemAllergen> Allergens { get; set; } = [];
    public ICollection<MealPrice> Prices { get; set; } = [];
}
public sealed class MealItemTranslation : Translation { public Guid MealItemId { get; set; } public MealItem MealItem { get; set; } = null!; public string Name { get; set; } = ""; public string? ShortDescription { get; set; } public string? FullDescription { get; set; } public string? PreparationInstructions { get; set; } public string? ServingNotes { get; set; } }
public sealed class MealNutrition : Entity { public Guid MealItemId { get; set; } public MealItem MealItem { get; set; } = null!; public decimal? ServingQuantity { get; set; } public string? ServingUnit { get; set; } public decimal? CaloriesKcal { get; set; } public decimal? ProteinGrams { get; set; } public decimal? CarbohydratesGrams { get; set; } public decimal? FatGrams { get; set; } public decimal? SaturatedFatGrams { get; set; } public decimal? TransFatGrams { get; set; } public decimal? FiberGrams { get; set; } public decimal? SugarGrams { get; set; } public decimal? SodiumMg { get; set; } public decimal? CholesterolMg { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } }
public static class MealMediaTypes
{
    public const string MealItem = "MEALITEM";
    public const string MealPlan = "MEALPLAN";
    public const string Thumbnail = "THUMBNAIL";
}

public sealed class MealMedia : Entity { public Guid EntityId { get; set; } public string MediaType { get; set; } = MealMediaTypes.MealItem; public string StorageProvider { get; set; } = "S3"; public string? BucketName { get; set; } public string ObjectKey { get; set; } = ""; public string? PublicUrl { get; set; } public string? ThumbnailObjectKey { get; set; } public string? ThumbnailUrl { get; set; } public string? MimeType { get; set; } public long? FileSizeBytes { get; set; } public int? WidthPixels { get; set; } public int? HeightPixels { get; set; } public bool IsPrimary { get; set; } public int DisplayOrder { get; set; } public string Status { get; set; } = "ACTIVE"; public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public Guid? CreatedBy { get; set; } public ICollection<MealMediaTranslation> Translations { get; set; } = []; }
public sealed class MealMediaTranslation : Translation { public Guid MealMediaId { get; set; } public MealMedia Media { get; set; } = null!; public string? AltText { get; set; } public string? Caption { get; set; } }

public sealed class Ingredient : Entity { public string Code { get; set; } = ""; public bool IsActive { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public Guid? CreatedBy { get; set; } public Guid? UpdatedBy { get; set; } public long RowVersion { get; set; } public ICollection<IngredientTranslation> Translations { get; set; } = []; }
public sealed class IngredientTranslation : Translation { public Guid IngredientId { get; set; } public Ingredient Ingredient { get; set; } = null!; public string Name { get; set; } = ""; public string? Description { get; set; } }
public sealed class MealItemIngredient : Entity { public Guid MealItemId { get; set; } public MealItem MealItem { get; set; } = null!; public Guid IngredientId { get; set; } public Ingredient Ingredient { get; set; } = null!; public decimal? Quantity { get; set; } public string? Unit { get; set; } public bool IsOptional { get; set; } public bool CanBeRemoved { get; set; } public bool CanBeReplaced { get; set; } public bool IsPrimaryIngredient { get; set; } public int DisplayOrder { get; set; } public DateTimeOffset CreatedAt { get; set; } }

public sealed class Allergen : Entity { public string Code { get; set; } = ""; public bool IsActive { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public Guid? CreatedBy { get; set; } public Guid? UpdatedBy { get; set; } public ICollection<AllergenTranslation> Translations { get; set; } = []; }
public sealed class AllergenTranslation : Translation { public Guid AllergenId { get; set; } public Allergen Allergen { get; set; } = null!; public string Name { get; set; } = ""; public string? Description { get; set; } }
public sealed class MealItemAllergen { public Guid MealItemId { get; set; } public MealItem MealItem { get; set; } = null!; public Guid AllergenId { get; set; } public Allergen Allergen { get; set; } = null!; public string AllergenLevel { get; set; } = "CONTAINS"; public DateTimeOffset CreatedAt { get; set; } }
public sealed class MealPrice : Entity { public Guid MealItemId { get; set; } public MealItem MealItem { get; set; } = null!; public Guid? BranchId { get; set; } public string PriceType { get; set; } = "INDIVIDUAL"; public string CurrencyCode { get; set; } = "QAR"; public decimal Amount { get; set; } public DateTimeOffset EffectiveFrom { get; set; } public DateTimeOffset? EffectiveUntil { get; set; } public bool IsActive { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public Guid? CreatedBy { get; set; } public Guid? UpdatedBy { get; set; } }
public sealed class MealType : Entity { public string Code { get; set; } = ""; public int DisplayOrder { get; set; } public bool IsActive { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public Guid? CreatedBy { get; set; } public Guid? UpdatedBy { get; set; } public ICollection<MealTypeTranslation> Translations { get; set; } = []; public ICollection<MealPackageOptionType> PackageOptions { get; set; } = []; public ICollection<MealPlanDayItem> PlanDayItems { get; set; } = []; }
public sealed class MealTypeTranslation : Translation { public Guid MealTypeId { get; set; } public MealType MealType { get; set; } = null!; public string Name { get; set; } = ""; public string? Description { get; set; } }

public sealed class MealPlanTemplate : Entity { public Guid VersionGroupId { get; set; } public int VersionNumber { get; set; } = 1; public bool IsLatest { get; set; } = true; public Guid? SupersedesId { get; set; } public string Code { get; set; } = ""; public string PlanType { get; set; } = "STANDARD"; public int DurationDays { get; set; } public Guid? CustomerId { get; set; } public bool IsCustomizable { get; set; } public bool IsPublished { get; set; } public bool IsActive { get; set; } public DateOnly? ValidFrom { get; set; } public DateOnly? ValidUntil { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public Guid? CreatedBy { get; set; } public Guid? UpdatedBy { get; set; } public long RowVersion { get; set; } public ICollection<MealPlanTemplateTranslation> Translations { get; set; } = []; public ICollection<MealPlanTemplateDay> Days { get; set; } = []; public ICollection<MealPlanWeekday> Weekdays { get; set; } = []; public ICollection<MealPlanPrice> Prices { get; set; } = []; }
public sealed class MealPlanTemplateTranslation : Translation { public Guid MealPlanTemplateId { get; set; } public MealPlanTemplate Plan { get; set; } = null!; public string Name { get; set; } = ""; public string? ShortDescription { get; set; } public string? FullDescription { get; set; } }
public sealed class MealPlanTemplateDay : Entity { public Guid MealPlanTemplateId { get; set; } public MealPlanTemplate Plan { get; set; } = null!; public MenuWeekday MenuWeekday { get; set; } public int DisplayOrder { get; set; } public bool IsActive { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public Guid? CreatedBy { get; set; } public Guid? UpdatedBy { get; set; } public ICollection<MealPlanTemplateSlot> Slots { get; set; } = []; }
public sealed class MealPlanTemplateSlot : Entity { public Guid MealPlanTemplateDayId { get; set; } public MealPlanTemplateDay Day { get; set; } = null!; public Guid MealTypeId { get; set; } public MealType MealType { get; set; } = null!; public int DisplayOrder { get; set; } public int MinimumSelection { get; set; } public int MaximumSelection { get; set; } public bool IsRequired { get; set; } public TimeOnly? SelectionCutoffTime { get; set; } public bool AllowsPaidUpgrade { get; set; } public bool IsActive { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public Guid? CreatedBy { get; set; } public Guid? UpdatedBy { get; set; } public long RowVersion { get; set; } public ICollection<MealPlanTemplateSlotTranslation> Translations { get; set; } = []; public ICollection<MealPlanSlotOption> Options { get; set; } = []; }
public sealed class MealPlanTemplateSlotTranslation : Translation { public Guid MealPlanTemplateSlotId { get; set; } public MealPlanTemplateSlot Slot { get; set; } = null!; public string? Title { get; set; } public string? Instruction { get; set; } }
public sealed class MealPlanSlotOption : Entity { public Guid MealPlanTemplateSlotId { get; set; } public MealPlanTemplateSlot Slot { get; set; } = null!; public Guid MealItemId { get; set; } public MealItem MealItem { get; set; } = null!; public decimal AdditionalPrice { get; set; } public bool IsDefault { get; set; } public bool IsAvailable { get; set; } public int DisplayOrder { get; set; } public DateTimeOffset? AvailableFrom { get; set; } public DateTimeOffset? AvailableUntil { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public Guid? CreatedBy { get; set; } public Guid? UpdatedBy { get; set; } }
public sealed class MealPlanPricePackage
{
    public string Code { get; set; } = "";
    public string NameEn { get; set; } = "";
    public string NameAr { get; set; } = "";
    public int DurationDays { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class MealPlanPrice : Entity
{
    public Guid MealPlanTemplateId { get; set; }
    public MealPlanTemplate Plan { get; set; } = null!;
    public Guid? DurationId { get; set; }
    public MealPlanDuration? Duration { get; set; }
    public Guid? PackageOptionId { get; set; }
    public MealPackageOption? PackageOption { get; set; }
    public int DurationDays { get; set; }
    public int MealsPerDay { get; set; }
    public int SnacksPerDay { get; set; }
    public string CurrencyCode { get; set; } = "QAR";
    public decimal Amount { get; set; }
    public DateTimeOffset EffectiveFrom { get; set; }
    public DateTimeOffset? EffectiveUntil { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public ICollection<MealPlanPriceTranslation> Translations { get; set; } = [];
}

public sealed class MealPlanPriceTranslation : Translation
{
    public Guid MealPlanPriceId { get; set; }
    public MealPlanPrice Price { get; set; } = null!;
    public string Name { get; set; } = "";
    public string? Description { get; set; }
}

public sealed class CustomerProfile : Entity
{
    public Guid? UserId { get; set; }
    public string? PreferredName { get; set; }
    public string? GenderCode { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public decimal? HeightCm { get; set; }
    public decimal? WeightKg { get; set; }
    public decimal? Bmi { get; set; }
    public string? BmiCategoryCode { get; set; }
    public string? GoalCode { get; set; }
    public string? DailyRoutineCode { get; set; }
    public string? ActivityLevelCode { get; set; }
    public string PreferredLanguage { get; set; } = "en";
    public string OnboardingStatus { get; set; } = "NOT_STARTED";
    public DateTimeOffset? OnboardingCompletedAt { get; set; }
    public bool AllergensConfirmed { get; set; }
    public bool PreferencesConfirmed { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public long RowVersion { get; set; }
    public ICollection<CustomerNutritionTarget> NutritionTargets { get; set; } = [];
    public ICollection<CustomerProfilePreference> Preferences { get; set; } = [];
    public ICollection<CustomerProfileAllergen> Allergens { get; set; } = [];
    public ICollection<CustomerAddress> Addresses { get; set; } = [];
}

public sealed class MealPlanDuration : Entity
{
    public string Name { get; set; } = "";
    public int DurationDays { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class MealPackageOption : Entity
{
    public string Name { get; set; } = "";
    public int MealCount { get; set; }
    public int SnackCount { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public ICollection<MealPackageOptionType> MealTypes { get; set; } = [];
    public ICollection<MealPlanPrice> Prices { get; set; } = [];
}

public sealed class MealPackageOptionType : Entity
{
    public Guid PackageOptionId { get; set; }
    public MealPackageOption MealPackageOption { get; set; } = null!;
    public Guid MealTypeId { get; set; }
    public MealType MealType { get; set; } = null!;
    public bool IsRequired { get; set; }
    public int MaxQuantity { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class MealPlanWeekday : Entity
{
    public Guid MealPlanId { get; set; }
    public MealPlanTemplate MealPlan { get; set; } = null!;
    public int DayOfWeek { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public ICollection<MealPlanDayItem> DayItems { get; set; } = [];
}

public sealed class MealPlanDayItem : Entity
{
    public Guid MealPlanWeekdayId { get; set; }
    public MealPlanWeekday MealPlanWeekday { get; set; } = null!;
    public Guid MealTypeId { get; set; }
    public MealType MealType { get; set; } = null!;
    public Guid MenuItemId { get; set; }
    public MealItem MenuItem { get; set; } = null!;
    public bool IsDefault { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
}

public static class CustomerGenderCodes
{
    public const string Male = "MALE";
    public const string Female = "FEMALE";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Male, Female], StringComparer.OrdinalIgnoreCase);
}

public static class CustomerAddressTypes
{
    public const string Home = "HOME";
    public const string Apartment = "APARTMENT";
    public const string Office = "OFFICE";
    public const string Other = "OTHER";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Home, Apartment, Office, Other], StringComparer.Ordinal);
}

public sealed class CustomerAddress : Entity
{
    public Guid CustomerProfileId { get; set; }
    public CustomerProfile CustomerProfile { get; set; } = null!;
    public string? AddressName { get; set; }
    public string AddressType { get; set; } = CustomerAddressTypes.Home;
    public string? BuildingNo { get; set; }
    public string? StreetNo { get; set; }
    public string? UnitNumber { get; set; }
    public string? ZoneNo { get; set; }
    public string Area { get; set; } = "";
    public string? Directions { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public string? FormattedAddress { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public long RowVersion { get; set; } = 1;
}

public sealed class DeliveryTimeSlot : Entity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string NameAr { get; set; } = "";
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public long RowVersion { get; set; } = 1;
}

public static class OrderStatuses
{
    public const string Confirmed = "CONFIRMED";
}

public static class PaymentStatuses
{
    public const string Pending = "PENDING";
}

public sealed class Order : Entity
{
    public string OrderNumber { get; set; } = "";
    public Guid CustomerProfileId { get; set; }
    public Guid MealPlanTemplateId { get; set; }
    public Guid MealPlanPriceId { get; set; }
    public Guid CustomerAddressId { get; set; }
    public Guid DeliveryTimeSlotId { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public int DeliveryDaysPerWeek { get; set; }
    public string PlanName { get; set; } = "";
    public string PlanDurationName { get; set; } = "";
    public decimal Subtotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal DeliveryCharge { get; set; }
    public decimal TotalAmount { get; set; }
    public string CurrencyCode { get; set; } = "QAR";
    public string? CouponCode { get; set; }
    public string? DeliveryAddressName { get; set; }
    public string DeliveryAddressType { get; set; } = "";
    public string? DeliveryBuildingNo { get; set; }
    public string? DeliveryStreetNo { get; set; }
    public string? DeliveryUnitNumber { get; set; }
    public string? DeliveryZoneNo { get; set; }
    public string DeliveryArea { get; set; } = "";
    public string? DeliveryDirections { get; set; }
    public decimal? DeliveryLatitude { get; set; }
    public decimal? DeliveryLongitude { get; set; }
    public string? DeliveryFormattedAddress { get; set; }
    public string DeliveryTimeSlotName { get; set; } = "";
    public TimeOnly DeliveryStartTime { get; set; }
    public TimeOnly DeliveryEndTime { get; set; }
    public string Status { get; set; } = OrderStatuses.Confirmed;
    public string PaymentStatus { get; set; } = PaymentStatuses.Pending;
    public DateTimeOffset PlacedAt { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public long RowVersion { get; set; } = 1;
    public ICollection<OrderMeal> Meals { get; set; } = [];
    public ICollection<OrderDeliveryDay> DeliveryDays { get; set; } = [];
    public ICollection<OrderStatusHistory> StatusHistory { get; set; } = [];
}

public sealed class OrderMeal : Entity
{
    public Guid OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public Guid MealTypeId { get; set; }
    public string MealTypeName { get; set; } = "";
    public int Quantity { get; set; }
}

public sealed class OrderDeliveryDay : Entity
{
    public Guid OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public int DayOfWeek { get; set; }
}

public sealed class OrderStatusHistory : Entity
{
    public Guid OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public string Status { get; set; } = "";
    public string? Notes { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
}

public sealed class CustomerNutritionTarget : Entity
{
    public Guid CustomerProfileId { get; set; }
    public CustomerProfile CustomerProfile { get; set; } = null!;
    public int? DailyCaloriesKcal { get; set; }
    public decimal? DailyProteinG { get; set; }
    public decimal? DailyCarbohydratesG { get; set; }
    public decimal? DailyFatG { get; set; }
    public decimal? DailyFiberG { get; set; }
    public int? DailyWaterMl { get; set; }
    public string? CalculationMethod { get; set; }
    public string? CalculationVersion { get; set; }
    public DateTimeOffset CalculatedAt { get; set; }
    public bool IsCurrent { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public long RowVersion { get; set; }
}

public sealed class CustomerProfilePreference : Entity
{
    public Guid CustomerProfileId { get; set; }
    public CustomerProfile CustomerProfile { get; set; } = null!;
    public string PreferenceCode { get; set; } = "";
    public string? PreferenceType { get; set; }
    public int PreferencePriority { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CustomerProfileAllergen : Entity
{
    public Guid CustomerProfileId { get; set; }
    public CustomerProfile CustomerProfile { get; set; } = null!;
    public Guid AllergenId { get; set; }
    public Allergen Allergen { get; set; } = null!;
    public string? SeverityCode { get; set; }
    public bool MedicallyConfirmed { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
}

// User Management Entities
public sealed class Customer : Entity
{
    public string CustomerName { get; set; } = "";
    public int? Age { get; set; }
    public string? Mobile { get; set; }
    public string? Email { get; set; }
    public string Status { get; set; } = "ACTIVE"; // ACTIVE, INACTIVE, SUSPENDED
    public bool IsActive { get; set; } = true;
    public decimal? Weight { get; set; }
    public decimal? Height { get; set; }
    public decimal? BMI { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string CreatedBy { get; set; } = "SYSTEM";
    public string? UpdatedBy { get; set; }
}

public sealed class UserProfile : Entity
{
    public Guid UserId { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Status { get; set; } = "ACTIVE"; // ACTIVE, INACTIVE, SUSPENDED
    public bool IsActive { get; set; } = true;
    public bool IsCustomer { get; set; } = false;
    public Guid? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public string? Mobile { get; set; }
    public string CreatedBy { get; set; } = "SYSTEM";
    public DateTimeOffset CreatedAt { get; set; }
    public string? ModifiedBy { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }

    public string FullName => $"{FirstName} {LastName}".Trim();
}

public sealed class ApplicationRole : Entity
{
    public string RoleName { get; set; } = "";
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string CreatedBy { get; set; } = "SYSTEM";
    public string? UpdatedBy { get; set; }
    public ICollection<RoleMenuMapping> MenuMappings { get; set; } = [];
}

public sealed class Menu : Entity
{
    public string MainMenuCode { get; set; } = "";
    public string MainMenuName { get; set; } = "";
    public string SubMenuCode { get; set; } = "";
    public string SubMenuName { get; set; } = "";
    public string? RouteUrl { get; set; }
    public string? Icon { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string CreatedBy { get; set; } = "SYSTEM";
    public string? UpdatedBy { get; set; }
    public ICollection<RoleMenuMapping> RoleMappings { get; set; } = [];
}

public sealed class RoleMenuMapping : Entity
{
    public Guid RoleId { get; set; }
    public ApplicationRole Role { get; set; } = null!;
    public Guid MenuId { get; set; }
    public Menu Menu { get; set; } = null!;
    public bool CanRead { get; set; } = true;
    public bool CanWrite { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class UserAttribute : Entity
{
    public Guid UserId { get; set; }
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // Common keys
    public const string PasswordKey = "PWD";
    public const string PasswordGenerationKey = "PWDGEN";
}
