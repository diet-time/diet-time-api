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
public sealed class MealType : Entity { public string Code { get; set; } = ""; public int DisplayOrder { get; set; } public bool IsActive { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public Guid? CreatedBy { get; set; } public Guid? UpdatedBy { get; set; } public ICollection<MealTypeTranslation> Translations { get; set; } = []; }
public sealed class MealTypeTranslation : Translation { public Guid MealTypeId { get; set; } public MealType MealType { get; set; } = null!; public string Name { get; set; } = ""; public string? Description { get; set; } }

public sealed class MealPlanTemplate : Entity { public Guid VersionGroupId { get; set; } public int VersionNumber { get; set; } = 1; public bool IsLatest { get; set; } = true; public Guid? SupersedesId { get; set; } public string Code { get; set; } = ""; public string PlanType { get; set; } = "STANDARD"; public int DurationDays { get; set; } public Guid? CustomerId { get; set; } public bool IsCustomizable { get; set; } public bool IsPublished { get; set; } public bool IsActive { get; set; } public DateOnly? ValidFrom { get; set; } public DateOnly? ValidUntil { get; set; } public int DisplayOrder { get; set; } public string? ImageUrl { get; set; } public string? IconUrl { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public Guid? CreatedBy { get; set; } public Guid? UpdatedBy { get; set; } public long RowVersion { get; set; } public ICollection<MealPlanTemplateTranslation> Translations { get; set; } = []; public ICollection<MealPlanTemplateDay> Days { get; set; } = []; public ICollection<MealPlanPrice> Prices { get; set; } = []; }
public sealed class MealPlanTemplateTranslation : Translation { public Guid MealPlanTemplateId { get; set; } public MealPlanTemplate Plan { get; set; } = null!; public string Name { get; set; } = ""; public string? ShortDescription { get; set; } public string? FullDescription { get; set; } }
public sealed class MealPlanTemplateDay : Entity { public Guid MealPlanTemplateId { get; set; } public MealPlanTemplate Plan { get; set; } = null!; public MenuWeekday MenuWeekday { get; set; } public int DisplayOrder { get; set; } public bool IsActive { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public Guid? CreatedBy { get; set; } public Guid? UpdatedBy { get; set; } public ICollection<MealPlanTemplateSlot> Slots { get; set; } = []; }
public sealed class MealPlanTemplateSlot : Entity { public Guid MealPlanTemplateDayId { get; set; } public MealPlanTemplateDay Day { get; set; } = null!; public Guid MealTypeId { get; set; } public MealType MealType { get; set; } = null!; public int DisplayOrder { get; set; } public int MinimumSelection { get; set; } public int MaximumSelection { get; set; } public bool IsRequired { get; set; } public TimeOnly? SelectionCutoffTime { get; set; } public bool AllowsPaidUpgrade { get; set; } public bool IsActive { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public Guid? CreatedBy { get; set; } public Guid? UpdatedBy { get; set; } public long RowVersion { get; set; } public ICollection<MealPlanTemplateSlotTranslation> Translations { get; set; } = []; public ICollection<MealPlanSlotOption> Options { get; set; } = []; }
public sealed class MealPlanTemplateSlotTranslation : Translation { public Guid MealPlanTemplateSlotId { get; set; } public MealPlanTemplateSlot Slot { get; set; } = null!; public string? Title { get; set; } public string? Instruction { get; set; } }
public sealed class MealPlanSlotOption : Entity { public Guid MealPlanTemplateSlotId { get; set; } public MealPlanTemplateSlot Slot { get; set; } = null!; public Guid MealItemId { get; set; } public MealItem MealItem { get; set; } = null!; public decimal AdditionalPrice { get; set; } public bool IsDefault { get; set; } public bool IsAvailable { get; set; } public int DisplayOrder { get; set; } public DateTimeOffset? AvailableFrom { get; set; } public DateTimeOffset? AvailableUntil { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public Guid? CreatedBy { get; set; } public Guid? UpdatedBy { get; set; } }
public sealed class MealPlanPrice : Entity { public Guid MealPlanTemplateId { get; set; } public MealPlanTemplate Plan { get; set; } = null!; public int DurationDays { get; set; } public int MealsPerDay { get; set; } public int SnacksPerDay { get; set; } public string CurrencyCode { get; set; } = "QAR"; public decimal Amount { get; set; } public DateTimeOffset EffectiveFrom { get; set; } public DateTimeOffset? EffectiveUntil { get; set; } public bool IsActive { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public Guid? CreatedBy { get; set; } public Guid? UpdatedBy { get; set; } }
