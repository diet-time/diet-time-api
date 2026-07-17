using DietTime.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Persistence;

public sealed class ApplicationUser : IdentityUser<Guid> { }
public sealed class RefreshToken : Entity { public Guid UserId { get; set; } public ApplicationUser User { get; set; } = null!; public string TokenHash { get; set; } = ""; public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset ExpiresAt { get; set; } public DateTimeOffset? RevokedAt { get; set; } }

public sealed class DietTimeDbContext(DbContextOptions<DietTimeDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<MealCategory> MealCategories => Set<MealCategory>();
    public DbSet<MealCategoryTranslation> MealCategoryTranslations => Set<MealCategoryTranslation>();
    public DbSet<MealItem> MealItems => Set<MealItem>();
    public DbSet<MealItemTranslation> MealItemTranslations => Set<MealItemTranslation>();
    public DbSet<MealNutrition> MealNutrition => Set<MealNutrition>();
    public DbSet<MealMedia> MealMedia => Set<MealMedia>();
    public DbSet<MealMediaTranslation> MealMediaTranslations => Set<MealMediaTranslation>();
    public DbSet<Ingredient> Ingredients => Set<Ingredient>();
    public DbSet<IngredientTranslation> IngredientTranslations => Set<IngredientTranslation>();
    public DbSet<MealItemIngredient> MealItemIngredients => Set<MealItemIngredient>();
    public DbSet<Allergen> Allergens => Set<Allergen>();
    public DbSet<AllergenTranslation> AllergenTranslations => Set<AllergenTranslation>();
    public DbSet<MealItemAllergen> MealItemAllergens => Set<MealItemAllergen>();
    public DbSet<MealPrice> MealPrices => Set<MealPrice>();
    public DbSet<MealType> MealTypes => Set<MealType>();
    public DbSet<MealTypeTranslation> MealTypeTranslations => Set<MealTypeTranslation>();
    public DbSet<MealPlanTemplate> MealPlanTemplates => Set<MealPlanTemplate>();
    public DbSet<MealPlanTemplateTranslation> MealPlanTemplateTranslations => Set<MealPlanTemplateTranslation>();
    public DbSet<MealPlanTemplateDay> MealPlanTemplateDays => Set<MealPlanTemplateDay>();
    public DbSet<MealPlanTemplateSlot> MealPlanTemplateSlots => Set<MealPlanTemplateSlot>();
    public DbSet<MealPlanTemplateSlotTranslation> MealPlanTemplateSlotTranslations => Set<MealPlanTemplateSlotTranslation>();
    public DbSet<MealPlanSlotOption> MealPlanSlotOptions => Set<MealPlanSlotOption>();
    public DbSet<MealPlanPrice> MealPlanPrices => Set<MealPlanPrice>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.Entity<RefreshToken>(e => { e.ToTable("refresh_tokens"); e.Property(x => x.TokenHash).HasMaxLength(64); e.HasIndex(x => x.TokenHash).IsUnique(); e.HasIndex(x => x.UserId); e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade); });
        ConfigureLookups(b); ConfigureMeals(b); ConfigurePlans(b);
    }

    private static void ConfigureLookups(ModelBuilder b)
    {
        b.Entity<MealCategory>(e => { e.ToTable("meal_categories"); e.Property(x => x.Code).HasMaxLength(50); e.HasIndex(x => x.Code).IsUnique(); e.Property(x => x.RowVersion).IsConcurrencyToken(); });
        Translation<MealCategoryTranslation>(b, "meal_category_translations", 10); b.Entity<MealCategoryTranslation>(e => { e.Property(x => x.Name).HasMaxLength(100); e.HasIndex(x => new { x.MealCategoryId, x.LanguageCode }).IsUnique(); e.HasOne(x => x.Category).WithMany(x => x.Translations).HasForeignKey(x => x.MealCategoryId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<Ingredient>(e => { e.ToTable("ingredients"); e.Property(x => x.Code).HasMaxLength(50); e.HasIndex(x => x.Code).IsUnique(); e.Property(x => x.RowVersion).IsConcurrencyToken(); });
        Translation<IngredientTranslation>(b, "ingredient_translations", 10); b.Entity<IngredientTranslation>(e => { e.Property(x => x.Name).HasMaxLength(150); e.HasIndex(x => new { x.IngredientId, x.LanguageCode }).IsUnique(); e.HasOne(x => x.Ingredient).WithMany(x => x.Translations).HasForeignKey(x => x.IngredientId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<Allergen>(e => { e.ToTable("allergens"); e.Property(x => x.Code).HasMaxLength(50); e.HasIndex(x => x.Code).IsUnique(); });
        Translation<AllergenTranslation>(b, "allergen_translations", 10); b.Entity<AllergenTranslation>(e => { e.Property(x => x.Name).HasMaxLength(100); e.HasIndex(x => new { x.AllergenId, x.LanguageCode }).IsUnique(); e.HasOne(x => x.Allergen).WithMany(x => x.Translations).HasForeignKey(x => x.AllergenId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<MealType>(e => { e.ToTable("meal_types"); e.Property(x => x.Code).HasMaxLength(50); e.HasIndex(x => x.Code).IsUnique(); });
        Translation<MealTypeTranslation>(b, "meal_type_translations", 10); b.Entity<MealTypeTranslation>(e => { e.Property(x => x.Name).HasMaxLength(100); e.HasIndex(x => new { x.MealTypeId, x.LanguageCode }).IsUnique(); e.HasOne(x => x.MealType).WithMany(x => x.Translations).HasForeignKey(x => x.MealTypeId).OnDelete(DeleteBehavior.Cascade); });
    }

    private static void ConfigureMeals(ModelBuilder b)
    {
        b.Entity<MealItem>(e => { e.ToTable("meal_items"); e.Property(x => x.Sku).HasMaxLength(50); e.Property(x => x.Status).HasMaxLength(30); e.Property(x => x.DefaultServingQuantity).HasPrecision(10, 2); e.Property(x => x.RowVersion).IsConcurrencyToken(); e.HasIndex(x => x.Sku).IsUnique(); e.HasIndex(x => x.CategoryId).HasDatabaseName("ix_meal_items_category"); e.HasIndex(x => x.Status).HasDatabaseName("ix_meal_items_status"); e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict); });
        Translation<MealItemTranslation>(b, "meal_item_translations", 10); b.Entity<MealItemTranslation>(e => { e.Property(x => x.Name).HasMaxLength(200); e.HasIndex(x => new { x.MealItemId, x.LanguageCode }).IsUnique(); e.HasIndex(x => x.LanguageCode).HasDatabaseName("ix_meal_item_translations_language"); e.HasOne(x => x.MealItem).WithMany(x => x.Translations).HasForeignKey(x => x.MealItemId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<MealNutrition>(e => { e.ToTable("meal_nutrition"); e.HasIndex(x => x.MealItemId).IsUnique(); foreach (var p in new[] { nameof(DietTime.Domain.MealNutrition.ServingQuantity), nameof(DietTime.Domain.MealNutrition.CaloriesKcal), nameof(DietTime.Domain.MealNutrition.ProteinGrams), nameof(DietTime.Domain.MealNutrition.CarbohydratesGrams), nameof(DietTime.Domain.MealNutrition.FatGrams), nameof(DietTime.Domain.MealNutrition.SaturatedFatGrams), nameof(DietTime.Domain.MealNutrition.TransFatGrams), nameof(DietTime.Domain.MealNutrition.FiberGrams), nameof(DietTime.Domain.MealNutrition.SugarGrams), nameof(DietTime.Domain.MealNutrition.SodiumMg), nameof(DietTime.Domain.MealNutrition.CholesterolMg) }) e.Property(p).HasPrecision(10, 2); e.HasOne(x => x.MealItem).WithOne(x => x.Nutrition).HasForeignKey<MealNutrition>(x => x.MealItemId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<MealMedia>(e => { e.ToTable("meal_media"); e.Property(x => x.ObjectKey).HasMaxLength(1000); e.Property(x => x.PublicUrl).HasMaxLength(2000); e.Property(x => x.ThumbnailUrl).HasMaxLength(2000); e.HasIndex(x => x.MealItemId).HasDatabaseName("ix_meal_media_meal"); e.HasIndex(x => x.MealItemId).IsUnique().HasFilter("is_primary = true AND status = 'ACTIVE'").HasDatabaseName("ux_meal_media_primary"); e.HasOne(x => x.MealItem).WithMany(x => x.Media).HasForeignKey(x => x.MealItemId).OnDelete(DeleteBehavior.Cascade); });
        Translation<MealMediaTranslation>(b, "meal_media_translations", 10); b.Entity<MealMediaTranslation>(e => { e.HasIndex(x => new { x.MealMediaId, x.LanguageCode }).IsUnique(); e.HasOne(x => x.Media).WithMany(x => x.Translations).HasForeignKey(x => x.MealMediaId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<MealItemIngredient>(e => { e.ToTable("meal_item_ingredients"); e.Property(x => x.Quantity).HasPrecision(10, 3); e.HasIndex(x => new { x.MealItemId, x.IngredientId }).IsUnique(); e.HasOne(x => x.MealItem).WithMany(x => x.Ingredients).HasForeignKey(x => x.MealItemId).OnDelete(DeleteBehavior.Cascade); e.HasOne(x => x.Ingredient).WithMany().HasForeignKey(x => x.IngredientId).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<MealItemAllergen>(e => { e.ToTable("meal_item_allergens"); e.HasKey(x => new { x.MealItemId, x.AllergenId }); e.HasOne(x => x.MealItem).WithMany(x => x.Allergens).HasForeignKey(x => x.MealItemId).OnDelete(DeleteBehavior.Cascade); e.HasOne(x => x.Allergen).WithMany().HasForeignKey(x => x.AllergenId).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<MealPrice>(e => { e.ToTable("meal_prices"); e.Property(x => x.Amount).HasPrecision(12, 2); e.Property(x => x.CurrencyCode).HasColumnType("char(3)"); e.HasOne(x => x.MealItem).WithMany(x => x.Prices).HasForeignKey(x => x.MealItemId).OnDelete(DeleteBehavior.Restrict); });
    }

    private static void ConfigurePlans(ModelBuilder b)
    {
        b.Entity<MealPlanTemplate>(e => { e.ToTable("meal_plan_templates"); e.HasIndex(x => x.Code).IsUnique(); e.Property(x => x.RowVersion).IsConcurrencyToken(); e.HasIndex(x => new { x.IsPublished, x.IsActive }).HasDatabaseName("ix_meal_plan_templates_published"); });
        Translation<MealPlanTemplateTranslation>(b, "meal_plan_template_translations", 10); b.Entity<MealPlanTemplateTranslation>(e => { e.HasIndex(x => new { x.MealPlanTemplateId, x.LanguageCode }).IsUnique(); e.HasOne(x => x.Plan).WithMany(x => x.Translations).HasForeignKey(x => x.MealPlanTemplateId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<MealPlanTemplateDay>(e => { e.ToTable("meal_plan_template_days"); e.Property(x => x.DisplayOrder).HasColumnName("display_order"); e.Property(x => x.MenuWeekday).HasColumnName("day_of_week").HasConversion(v => v.ToString().ToUpperInvariant(), v => Enum.Parse<MenuWeekday>(v, true)).HasMaxLength(20); e.HasIndex(x => new { x.MealPlanTemplateId, x.MenuWeekday }).IsUnique(); e.HasIndex(x => x.MealPlanTemplateId).HasDatabaseName("ix_meal_plan_days_template"); e.HasOne(x => x.Plan).WithMany(x => x.Days).HasForeignKey(x => x.MealPlanTemplateId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<MealPlanTemplateSlot>(e => { e.ToTable("meal_plan_template_slots"); e.Property(x => x.RowVersion).IsConcurrencyToken(); e.HasIndex(x => new { x.MealPlanTemplateDayId, x.MealTypeId }).IsUnique(); e.HasOne(x => x.Day).WithMany(x => x.Slots).HasForeignKey(x => x.MealPlanTemplateDayId).OnDelete(DeleteBehavior.Cascade); e.HasOne(x => x.MealType).WithMany().HasForeignKey(x => x.MealTypeId).OnDelete(DeleteBehavior.Restrict); });
        Translation<MealPlanTemplateSlotTranslation>(b, "meal_plan_template_slot_translations", 10); b.Entity<MealPlanTemplateSlotTranslation>(e => { e.HasIndex(x => new { x.MealPlanTemplateSlotId, x.LanguageCode }).IsUnique(); e.HasOne(x => x.Slot).WithMany(x => x.Translations).HasForeignKey(x => x.MealPlanTemplateSlotId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<MealPlanSlotOption>(e => { e.ToTable("meal_plan_slot_options"); e.Property(x => x.AdditionalPrice).HasPrecision(12, 2); e.HasIndex(x => x.MealPlanTemplateSlotId).HasDatabaseName("ix_meal_plan_slot_options_slot"); e.HasIndex(x => x.MealItemId).HasDatabaseName("ix_meal_plan_slot_options_meal"); e.HasOne(x => x.Slot).WithMany(x => x.Options).HasForeignKey(x => x.MealPlanTemplateSlotId).OnDelete(DeleteBehavior.Cascade); e.HasOne(x => x.MealItem).WithMany().HasForeignKey(x => x.MealItemId).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<MealPlanPrice>(e => { e.ToTable("meal_plan_prices"); e.Property(x => x.Amount).HasPrecision(12, 2); e.Property(x => x.CurrencyCode).HasColumnType("char(3)"); e.HasOne(x => x.Plan).WithMany(x => x.Prices).HasForeignKey(x => x.MealPlanTemplateId).OnDelete(DeleteBehavior.Restrict); });
    }

    private static void Translation<T>(ModelBuilder b, string table, int languageLength) where T : Translation
    {
        b.Entity<T>(e => { e.ToTable(table); e.Property(x => x.LanguageCode).HasMaxLength(languageLength); });
    }
}
