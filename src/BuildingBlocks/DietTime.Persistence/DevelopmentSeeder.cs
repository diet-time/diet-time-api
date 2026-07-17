using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Persistence;

public static class DevelopmentSeeder
{
    public static async Task SeedAsync(DietTimeDbContext db, CancellationToken ct = default)
    {
        if (await db.MealTypes.AnyAsync(ct)) return;
        var now = DateTimeOffset.UtcNow;
        var category = new MealCategory { Id = Id(1), Code = "BREAKFAST", DisplayOrder = 1, IsActive = true, CreatedAt = now, UpdatedAt = now, RowVersion = 1, Translations = [CategoryText("en", "Breakfast", now), CategoryText("ar", "الإفطار", now)] };
        var type = new MealType { Id = Id(2), Code = "BREAKFAST", DisplayOrder = 1, IsActive = true, CreatedAt = now, UpdatedAt = now, Translations = [TypeText("en", "Breakfast", now), TypeText("ar", "الإفطار", now)] };
        var ingredient = new Ingredient { Id = Id(3), Code = "CHIA_SEEDS", IsActive = true, CreatedAt = now, UpdatedAt = now, RowVersion = 1, Translations = [new() { LanguageCode = "en", Name = "Chia Seeds", CreatedAt = now, UpdatedAt = now }, new() { LanguageCode = "ar", Name = "بذور الشيا", CreatedAt = now, UpdatedAt = now }] };
        var allergen = new Allergen { Id = Id(4), Code = "TREE_NUTS", IsActive = true, CreatedAt = now, UpdatedAt = now, Translations = [new() { LanguageCode = "en", Name = "Tree Nuts", CreatedAt = now, UpdatedAt = now }, new() { LanguageCode = "ar", Name = "المكسرات", CreatedAt = now, UpdatedAt = now }] };
        var meal = new MealItem { Id = Id(6), Sku = "DT-BRK-0001", Category = category, PreparationTimeMinutes = 15, IsVegetarian = true, IsAvailable = true, Status = "ACTIVE", CreatedAt = now, UpdatedAt = now, RowVersion = 1, Translations = [new() { LanguageCode = "en", Name = "Coconut Chia Pudding", ShortDescription = "Chia pudding with coconut milk", CreatedAt = now, UpdatedAt = now }, new() { LanguageCode = "ar", Name = "بودينغ الشيا بجوز الهند", ShortDescription = "بودينغ الشيا مع حليب جوز الهند", CreatedAt = now, UpdatedAt = now }], Nutrition = new() { Id = Id(7), ServingQuantity = 350, ServingUnit = "g", CaloriesKcal = 324, ProteinGrams = 9, CarbohydratesGrams = 22, FatGrams = 24, CreatedAt = now, UpdatedAt = now } };
        meal.Media.Add(new() { Id = Id(8), ObjectKey = "meals/demo/coconut-chia.webp", ThumbnailObjectKey = "meals/demo/coconut-chia-thumb.webp", StorageProvider = "S3", MimeType = "image/webp", IsPrimary = true, Status = "ACTIVE", CreatedAt = now, UpdatedAt = now });
        meal.Ingredients.Add(new() { Id = Id(9), Ingredient = ingredient, Quantity = 30, Unit = "g", IsPrimaryIngredient = true, CreatedAt = now }); meal.Allergens.Add(new() { Allergen = allergen, AllergenLevel = "MAY_CONTAIN", CreatedAt = now }); meal.Prices.Add(new() { Id = Id(10), Amount = 25, CurrencyCode = "QAR", PriceType = "INDIVIDUAL", EffectiveFrom = now.AddDays(-1), IsActive = true, CreatedAt = now, UpdatedAt = now });
        var plan = new MealPlanTemplate { Id = Id(11), Code = "DT-CLASSIC-001", PlanType = "STANDARD", DurationDays = 7, IsCustomizable = true, IsPublished = true, IsActive = true, ValidFrom = DateOnly.FromDateTime(now.UtcDateTime), CreatedAt = now, UpdatedAt = now, RowVersion = 1, Translations = [new() { LanguageCode = "en", Name = "Classic", ShortDescription = "Balanced everyday meals", CreatedAt = now, UpdatedAt = now }, new() { LanguageCode = "ar", Name = "كلاسيك", ShortDescription = "وجبات يومية متوازنة", CreatedAt = now, UpdatedAt = now }] };
        var day = new MealPlanTemplateDay { Id = Id(12), MenuWeekday = MenuWeekday.Saturday, DisplayOrder = 1, IsActive = true, CreatedAt = now, UpdatedAt = now }; var slot = new MealPlanTemplateSlot { Id = Id(13), MealType = type, MinimumSelection = 1, MaximumSelection = 1, IsRequired = true, AllowsPaidUpgrade = true, IsActive = true, CreatedAt = now, UpdatedAt = now, RowVersion = 1 }; slot.Options.Add(new() { Id = Id(14), MealItem = meal, IsDefault = true, IsAvailable = true, CreatedAt = now, UpdatedAt = now }); day.Slots.Add(slot); plan.Days.Add(day); plan.Prices.Add(new() { Id = Id(15), DurationDays = 7, MealsPerDay = 3, SnacksPerDay = 1, Amount = 650, CurrencyCode = "QAR", EffectiveFrom = now.AddDays(-1), IsActive = true, CreatedAt = now, UpdatedAt = now });
        db.Add(plan); await db.SaveChangesAsync(ct);
    }
    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");
    private static MealCategoryTranslation CategoryText(string language, string name, DateTimeOffset now) => new() { LanguageCode = language, Name = name, CreatedAt = now, UpdatedAt = now };
    private static MealTypeTranslation TypeText(string language, string name, DateTimeOffset now) => new() { LanguageCode = language, Name = name, CreatedAt = now, UpdatedAt = now };
}
