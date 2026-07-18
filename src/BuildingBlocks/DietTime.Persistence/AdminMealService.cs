using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace DietTime.Persistence;

public sealed class AdminMealService(DietTimeDbContext db, TimeProvider clock, IMemoryCache cache, IOptions<DeliveryScheduleOptions> deliverySchedule) : IAdminMealService, ITemplateMenuReader
{
    private const int ExpiringMealHorizonDays = 7;

    public async Task<AdminDashboardResponse> GetDashboardAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var horizon = now.AddDays(ExpiringMealHorizonDays);

        var meals = db.MealItems.AsNoTracking();
        var activeMeals = await meals.CountAsync(x => x.Status == "ACTIVE", ct);
        var draftMeals = await meals.CountAsync(x => x.Status == "DRAFT", ct);
        var unavailableMeals = await meals.CountAsync(x => x.Status != "ARCHIVED" && !x.IsAvailable, ct);
        var expiringMeals = await meals.CountAsync(
            x => x.Status != "ARCHIVED" && x.AvailableUntil >= now && x.AvailableUntil < horizon,
            ct);
        var missingImages = await meals.CountAsync(
            x => x.Status != "ARCHIVED"
                && !db.MealMedia.Any(m => m.MealItemId == x.Id && m.Status == "ACTIVE" && m.MediaType == "IMAGE"),
            ct);
        var missingArabic = await meals.CountAsync(
            x => x.Status != "ARCHIVED"
                && !db.MealItemTranslations.Any(t => t.MealItemId == x.Id && t.LanguageCode == "ar"),
            ct);
        var missingNutrition = await meals.CountAsync(
            x => x.Status != "ARCHIVED" && !db.MealNutrition.Any(n => n.MealItemId == x.Id),
            ct);

        var publishedPlans = await db.MealPlanTemplates.AsNoTracking()
            .CountAsync(x => x.IsActive && x.IsPublished, ct);
        var draftPlans = await db.MealPlanTemplates.AsNoTracking()
            .CountAsync(x => x.IsActive && !x.IsPublished, ct);

        var scheduledMealPriceChanges = await db.MealPrices.AsNoTracking()
            .CountAsync(x => x.IsActive && x.EffectiveFrom > now, ct);
        var scheduledPlanPriceChanges = await db.MealPlanPrices.AsNoTracking()
            .CountAsync(x => x.IsActive && x.EffectiveFrom > now, ct);

        var mealsByCategory = await db.MealCategories.AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.DisplayOrder)
            .ThenBy(x => x.Code)
            .Select(x => new DashboardMetricResponse(
                x.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault() ?? x.Code,
                db.MealItems.Count(m => m.CategoryId == x.Id && m.Status != "ARCHIVED")))
            .ToListAsync(ct);

        return new AdminDashboardResponse(
            activeMeals,
            draftMeals,
            unavailableMeals,
            publishedPlans,
            draftPlans,
            expiringMeals,
            scheduledMealPriceChanges + scheduledPlanPriceChanges,
            missingImages,
            missingArabic,
            missingNutrition,
            mealsByCategory);
    }

    public async Task<PagedResult<AdminAllergenResponse>> GetAllergensAsync(string? search, string? sort, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Allergens.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => EF.Functions.ILike(x.Code, $"%{term}%")
                || x.Translations.Any(t => EF.Functions.ILike(t.Name, $"%{term}%")));
        }

        var totalCount = await query.CountAsync(ct);
        var (sortField, descending) = ParseSort(sort);
        var ordered = (sortField, descending) switch
        {
            ("nameen", false) => query.OrderBy(x => x.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault()),
            ("nameen", true) => query.OrderByDescending(x => x.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault()),
            ("namear", false) => query.OrderBy(x => x.Translations.Where(t => t.LanguageCode == "ar").Select(t => t.Name).FirstOrDefault()),
            ("namear", true) => query.OrderByDescending(x => x.Translations.Where(t => t.LanguageCode == "ar").Select(t => t.Name).FirstOrDefault()),
            ("isactive", false) => query.OrderBy(x => x.IsActive),
            ("isactive", true) => query.OrderByDescending(x => x.IsActive),
            ("usagecount", false) => query.OrderBy(x => db.MealItemAllergens.Count(link => link.AllergenId == x.Id)),
            ("usagecount", true) => query.OrderByDescending(x => db.MealItemAllergens.Count(link => link.AllergenId == x.Id)),
            ("createdat", false) => query.OrderBy(x => x.CreatedAt),
            ("createdat", true) => query.OrderByDescending(x => x.CreatedAt),
            ("updatedat", false) => query.OrderBy(x => x.UpdatedAt),
            ("updatedat", true) => query.OrderByDescending(x => x.UpdatedAt),
            (_, true) => query.OrderByDescending(x => x.Code),
            _ => query.OrderBy(x => x.Code)
        };

        var items = await ordered.ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new AdminAllergenResponse(
                x.Id,
                x.Code,
                x.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault() ?? "",
                x.Translations.Where(t => t.LanguageCode == "ar").Select(t => t.Name).FirstOrDefault() ?? "",
                x.IsActive,
                db.MealItemAllergens.Count(link => link.AllergenId == x.Id),
                x.CreatedAt,
                x.UpdatedAt))
            .ToListAsync(ct);

        return new(items, new(page, pageSize, totalCount, (int)Math.Ceiling(totalCount / (double)pageSize)));
    }

    public async Task<Guid?> CreateAllergenAsync(UpsertAllergenRequest request, Guid? userId, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Allergens.AnyAsync(x => x.Code == code, ct)) return null;

        var now = clock.GetUtcNow();
        var allergen = new Allergen
        {
            Code = code,
            IsActive = request.IsActive,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = userId,
            UpdatedBy = userId
        };
        allergen.Translations.Add(AllergenTranslation("en", request.NameEn, now));
        allergen.Translations.Add(AllergenTranslation("ar", request.NameAr, now));
        db.Allergens.Add(allergen);
        await db.SaveChangesAsync(ct);
        return allergen.Id;
    }

    public async Task<AdminWriteResult> UpdateAllergenAsync(Guid allergenId, UpsertAllergenRequest request, Guid? userId, CancellationToken ct)
    {
        var allergen = await db.Allergens.Include(x => x.Translations).SingleOrDefaultAsync(x => x.Id == allergenId, ct);
        if (allergen is null) return AdminWriteResult.NotFound;

        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Allergens.AnyAsync(x => x.Id != allergenId && x.Code == code, ct)) return AdminWriteResult.Conflict;

        var now = clock.GetUtcNow();
        allergen.Code = code;
        allergen.IsActive = request.IsActive;
        allergen.UpdatedAt = now;
        allergen.UpdatedBy = userId;
        SetAllergenTranslation(allergen, "en", request.NameEn, now);
        SetAllergenTranslation(allergen, "ar", request.NameAr, now);
        await db.SaveChangesAsync(ct);
        return AdminWriteResult.Success;
    }

    private static AllergenTranslation AllergenTranslation(string languageCode, string name, DateTimeOffset now) => new()
    {
        LanguageCode = languageCode,
        Name = name.Trim(),
        CreatedAt = now,
        UpdatedAt = now
    };

    private static void SetAllergenTranslation(Allergen allergen, string languageCode, string name, DateTimeOffset now)
    {
        var translation = allergen.Translations.SingleOrDefault(x => x.LanguageCode == languageCode);
        if (translation is null)
        {
            allergen.Translations.Add(AllergenTranslation(languageCode, name, now));
            return;
        }

        translation.Name = name.Trim();
        translation.UpdatedAt = now;
    }

    public async Task<PagedResult<AdminIngredientResponse>> GetIngredientsAsync(string? search, string? sort, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Ingredients.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => EF.Functions.ILike(x.Code, $"%{term}%")
                || x.Translations.Any(t => EF.Functions.ILike(t.Name, $"%{term}%")));
        }

        var totalCount = await query.CountAsync(ct);
        var (sortField, descending) = ParseSort(sort);
        var ordered = (sortField, descending) switch
        {
            ("nameen", false) => query.OrderBy(x => x.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault()),
            ("nameen", true) => query.OrderByDescending(x => x.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault()),
            ("namear", false) => query.OrderBy(x => x.Translations.Where(t => t.LanguageCode == "ar").Select(t => t.Name).FirstOrDefault()),
            ("namear", true) => query.OrderByDescending(x => x.Translations.Where(t => t.LanguageCode == "ar").Select(t => t.Name).FirstOrDefault()),
            ("isactive", false) => query.OrderBy(x => x.IsActive),
            ("isactive", true) => query.OrderByDescending(x => x.IsActive),
            ("usagecount", false) => query.OrderBy(x => db.MealItemIngredients.Count(link => link.IngredientId == x.Id)),
            ("usagecount", true) => query.OrderByDescending(x => db.MealItemIngredients.Count(link => link.IngredientId == x.Id)),
            ("createdat", false) => query.OrderBy(x => x.CreatedAt),
            ("createdat", true) => query.OrderByDescending(x => x.CreatedAt),
            ("updatedat", false) => query.OrderBy(x => x.UpdatedAt),
            ("updatedat", true) => query.OrderByDescending(x => x.UpdatedAt),
            (_, true) => query.OrderByDescending(x => x.Code),
            _ => query.OrderBy(x => x.Code)
        };

        var items = await ordered.ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new AdminIngredientResponse(
                x.Id,
                x.Code,
                x.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault() ?? "",
                x.Translations.Where(t => t.LanguageCode == "ar").Select(t => t.Name).FirstOrDefault() ?? "",
                x.IsActive,
                db.MealItemIngredients.Count(link => link.IngredientId == x.Id),
                x.CreatedAt,
                x.UpdatedAt))
            .ToListAsync(ct);

        return new(items, new(page, pageSize, totalCount, (int)Math.Ceiling(totalCount / (double)pageSize)));
    }

    public async Task<Guid?> CreateIngredientAsync(UpsertIngredientRequest request, Guid? userId, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Ingredients.AnyAsync(x => x.Code == code, ct)) return null;

        var now = clock.GetUtcNow();
        var ingredient = new Ingredient
        {
            Code = code,
            IsActive = request.IsActive,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = userId,
            UpdatedBy = userId,
            RowVersion = 1
        };
        ingredient.Translations.Add(IngredientTranslation("en", request.NameEn, now));
        ingredient.Translations.Add(IngredientTranslation("ar", request.NameAr, now));
        db.Ingredients.Add(ingredient);
        await db.SaveChangesAsync(ct);
        return ingredient.Id;
    }

    public async Task<AdminWriteResult> UpdateIngredientAsync(Guid ingredientId, UpsertIngredientRequest request, Guid? userId, CancellationToken ct)
    {
        var ingredient = await db.Ingredients.Include(x => x.Translations).SingleOrDefaultAsync(x => x.Id == ingredientId, ct);
        if (ingredient is null) return AdminWriteResult.NotFound;

        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Ingredients.AnyAsync(x => x.Id != ingredientId && x.Code == code, ct)) return AdminWriteResult.Conflict;

        var now = clock.GetUtcNow();
        ingredient.Code = code;
        ingredient.IsActive = request.IsActive;
        ingredient.UpdatedAt = now;
        ingredient.UpdatedBy = userId;
        ingredient.RowVersion++;
        SetIngredientTranslation(ingredient, "en", request.NameEn, now);
        SetIngredientTranslation(ingredient, "ar", request.NameAr, now);
        await db.SaveChangesAsync(ct);
        return AdminWriteResult.Success;
    }

    private static IngredientTranslation IngredientTranslation(string languageCode, string name, DateTimeOffset now) => new()
    {
        LanguageCode = languageCode,
        Name = name.Trim(),
        CreatedAt = now,
        UpdatedAt = now
    };

    private static void SetIngredientTranslation(Ingredient ingredient, string languageCode, string name, DateTimeOffset now)
    {
        var translation = ingredient.Translations.SingleOrDefault(x => x.LanguageCode == languageCode);
        if (translation is null)
        {
            ingredient.Translations.Add(IngredientTranslation(languageCode, name, now));
            return;
        }

        translation.Name = name.Trim();
        translation.UpdatedAt = now;
    }

    public async Task<PagedResult<AdminMealCategoryResponse>> GetMealCategoriesAsync(string? search, string? sort, int page, int pageSize, CancellationToken ct)
    {
        var query = db.MealCategories.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => EF.Functions.ILike(x.Code, $"%{term}%")
                || x.Translations.Any(t => EF.Functions.ILike(t.Name, $"%{term}%")));
        }

        var totalCount = await query.CountAsync(ct);
        var (sortField, descending) = ParseSort(sort);
        var ordered = (sortField, descending) switch
        {
            ("nameen", false) => query.OrderBy(x => x.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault()),
            ("nameen", true) => query.OrderByDescending(x => x.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault()),
            ("namear", false) => query.OrderBy(x => x.Translations.Where(t => t.LanguageCode == "ar").Select(t => t.Name).FirstOrDefault()),
            ("namear", true) => query.OrderByDescending(x => x.Translations.Where(t => t.LanguageCode == "ar").Select(t => t.Name).FirstOrDefault()),
            ("displayorder", false) => query.OrderBy(x => x.DisplayOrder),
            ("displayorder", true) => query.OrderByDescending(x => x.DisplayOrder),
            ("isactive", false) => query.OrderBy(x => x.IsActive),
            ("isactive", true) => query.OrderByDescending(x => x.IsActive),
            ("usagecount", false) => query.OrderBy(x => db.MealItems.Count(meal => meal.CategoryId == x.Id)),
            ("usagecount", true) => query.OrderByDescending(x => db.MealItems.Count(meal => meal.CategoryId == x.Id)),
            ("createdat", false) => query.OrderBy(x => x.CreatedAt),
            ("createdat", true) => query.OrderByDescending(x => x.CreatedAt),
            ("updatedat", false) => query.OrderBy(x => x.UpdatedAt),
            ("updatedat", true) => query.OrderByDescending(x => x.UpdatedAt),
            (_, true) => query.OrderByDescending(x => x.Code),
            _ => query.OrderBy(x => x.Code)
        };

        var items = await ordered.ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new AdminMealCategoryResponse(
                x.Id,
                x.Code,
                x.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault() ?? "",
                x.Translations.Where(t => t.LanguageCode == "ar").Select(t => t.Name).FirstOrDefault() ?? "",
                x.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Description).FirstOrDefault(),
                x.Translations.Where(t => t.LanguageCode == "ar").Select(t => t.Description).FirstOrDefault(),
                x.DisplayOrder,
                x.IsActive,
                db.MealItems.Count(meal => meal.CategoryId == x.Id),
                x.CreatedAt,
                x.UpdatedAt))
            .ToListAsync(ct);

        return new(items, new(page, pageSize, totalCount, (int)Math.Ceiling(totalCount / (double)pageSize)));
    }

    public async Task<Guid?> CreateMealCategoryAsync(UpsertMealCategoryRequest request, Guid? userId, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.MealCategories.AnyAsync(x => x.Code == code, ct)) return null;

        var now = clock.GetUtcNow();
        var category = new MealCategory
        {
            Code = code,
            DisplayOrder = request.DisplayOrder,
            IsActive = request.IsActive,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = userId,
            UpdatedBy = userId,
            RowVersion = 1
        };
        category.Translations.Add(MealCategoryTranslation("en", request.NameEn, request.DescriptionEn, now));
        category.Translations.Add(MealCategoryTranslation("ar", request.NameAr, request.DescriptionAr, now));
        db.MealCategories.Add(category);
        await db.SaveChangesAsync(ct);
        return category.Id;
    }

    public async Task<AdminWriteResult> UpdateMealCategoryAsync(Guid categoryId, UpsertMealCategoryRequest request, Guid? userId, CancellationToken ct)
    {
        var category = await db.MealCategories.Include(x => x.Translations).SingleOrDefaultAsync(x => x.Id == categoryId, ct);
        if (category is null) return AdminWriteResult.NotFound;

        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.MealCategories.AnyAsync(x => x.Id != categoryId && x.Code == code, ct)) return AdminWriteResult.Conflict;

        var now = clock.GetUtcNow();
        category.Code = code;
        category.DisplayOrder = request.DisplayOrder;
        category.IsActive = request.IsActive;
        category.UpdatedAt = now;
        category.UpdatedBy = userId;
        category.RowVersion++;
        SetMealCategoryTranslation(category, "en", request.NameEn, request.DescriptionEn, now);
        SetMealCategoryTranslation(category, "ar", request.NameAr, request.DescriptionAr, now);
        await db.SaveChangesAsync(ct);
        return AdminWriteResult.Success;
    }

    private static MealCategoryTranslation MealCategoryTranslation(string languageCode, string name, string? description, DateTimeOffset now) => new()
    {
        LanguageCode = languageCode,
        Name = name.Trim(),
        Description = description?.Trim(),
        CreatedAt = now,
        UpdatedAt = now
    };

    private static void SetMealCategoryTranslation(MealCategory category, string languageCode, string name, string? description, DateTimeOffset now)
    {
        var translation = category.Translations.SingleOrDefault(x => x.LanguageCode == languageCode);
        if (translation is null)
        {
            category.Translations.Add(MealCategoryTranslation(languageCode, name, description, now));
            return;
        }

        translation.Name = name.Trim();
        translation.Description = description?.Trim();
        translation.UpdatedAt = now;
    }

    private static (string Field, bool Descending) ParseSort(string? sort)
    {
        var value = sort?.Trim().ToLowerInvariant() ?? "";
        var descending = value.StartsWith('-') || value.EndsWith(":desc") || value.EndsWith("_desc") || value.EndsWith(" desc");
        value = value.TrimStart('-');
        foreach (var suffix in new[] { ":asc", ":desc", "_asc", "_desc", " asc", " desc" })
            if (value.EndsWith(suffix)) value = value[..^suffix.Length];
        return (value, descending);
    }

    public async Task<PagedResult<AdminMealTypeResponse>> GetMealTypesAsync(string? search, string? sort, int page, int pageSize, CancellationToken ct)
    {
        var query = db.MealTypes.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => EF.Functions.ILike(x.Code, $"%{term}%")
                || x.Translations.Any(t => EF.Functions.ILike(t.Name, $"%{term}%")));
        }

        var totalCount = await query.CountAsync(ct);
        var (sortField, descending) = ParseSort(sort);
        var ordered = (sortField, descending) switch
        {
            ("code", true) => query.OrderByDescending(x => x.Code),
            ("nameen", false) => query.OrderBy(x => x.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault()),
            ("nameen", true) => query.OrderByDescending(x => x.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault()),
            ("displayorder", false) => query.OrderBy(x => x.DisplayOrder),
            ("displayorder", true) => query.OrderByDescending(x => x.DisplayOrder),
            ("isactive", false) => query.OrderBy(x => x.IsActive),
            ("isactive", true) => query.OrderByDescending(x => x.IsActive),
            ("createdat", false) => query.OrderBy(x => x.CreatedAt),
            ("createdat", true) => query.OrderByDescending(x => x.CreatedAt),
            ("updatedat", false) => query.OrderBy(x => x.UpdatedAt),
            ("updatedat", true) => query.OrderByDescending(x => x.UpdatedAt),
            _ => query.OrderBy(x => x.Code)
        };

        var items = await ordered.ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new AdminMealTypeResponse(
                x.Id,
                x.Code,
                x.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault() ?? "",
                x.Translations.Where(t => t.LanguageCode == "ar").Select(t => t.Name).FirstOrDefault() ?? "",
                x.DisplayOrder,
                x.IsActive,
                x.CreatedAt,
                x.UpdatedAt))
            .ToListAsync(ct);

        return new(items, new(page, pageSize, totalCount, (int)Math.Ceiling(totalCount / (double)pageSize)));
    }

    public async Task<AdminWriteResult> UpdateMealTypeAsync(Guid mealTypeId, UpsertMealTypeRequest request, Guid? userId, CancellationToken ct)
    {
        var mealType = await db.MealTypes.Include(x => x.Translations).SingleOrDefaultAsync(x => x.Id == mealTypeId, ct);
        if (mealType is null) return AdminWriteResult.NotFound;

        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.MealTypes.AnyAsync(x => x.Id != mealTypeId && x.Code == code, ct)) return AdminWriteResult.Conflict;

        var now = clock.GetUtcNow();
        mealType.Code = code;
        mealType.DisplayOrder = request.DisplayOrder;
        mealType.IsActive = request.IsActive;
        mealType.UpdatedAt = now;
        mealType.UpdatedBy = userId;

        var translation = mealType.Translations.SingleOrDefault(x => x.LanguageCode == "en");
        if (translation is null)
        {
            mealType.Translations.Add(new MealTypeTranslation
            {
                LanguageCode = "en",
                Name = request.NameEn.Trim(),
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            translation.Name = request.NameEn.Trim();
            translation.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        cache.Remove("meal-types:en");
        cache.Remove("meal-types:ar");
        return AdminWriteResult.Success;
    }

    public async Task<PagedResult<AdminMealSummaryResponse>> GetMealsAsync(string? search, int page, int pageSize, CancellationToken ct)
    {
        var query = db.MealItems.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => EF.Functions.ILike(x.Sku, $"%{term}%") || x.Translations.Any(t => EF.Functions.ILike(t.Name, $"%{term}%")));
        }
        var count = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.UpdatedAt).Skip((page - 1) * pageSize).Take(pageSize).Select(x => new AdminMealSummaryResponse(x.Id, x.Sku, x.Status, x.IsAvailable, x.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault() ?? x.Translations.Select(t => t.Name).FirstOrDefault() ?? x.Sku, x.UpdatedAt)).ToListAsync(ct);
        return new(rows, new(page, pageSize, count, (int)Math.Ceiling(count / (double)pageSize)));
    }

    public async Task<AdminMealResponse?> GetMealAsync(Guid mealId, CancellationToken ct)
    {
        var x = await db.MealItems.AsNoTracking().AsSplitQuery().Include(m => m.Translations).Include(m => m.Nutrition).Include(m => m.Ingredients).Include(m => m.Allergens).Include(m => m.Prices).Include(m => m.Media).ThenInclude(m => m.Translations).SingleOrDefaultAsync(m => m.Id == mealId, ct);
        if (x is null) return null;
        var translations = x.Translations.Select(t => new AdminTranslationRequest(t.LanguageCode, t.Name, t.ShortDescription, t.FullDescription, t.PreparationInstructions, t.ServingNotes)).ToArray();
        var nutrition = x.Nutrition is null ? null : new AdminNutritionRequest(x.Nutrition.ServingQuantity, x.Nutrition.ServingUnit, x.Nutrition.CaloriesKcal, x.Nutrition.ProteinGrams, x.Nutrition.CarbohydratesGrams, x.Nutrition.FatGrams, x.Nutrition.SaturatedFatGrams,x.Nutrition.TransFatGrams, x.Nutrition.FiberGrams, x.Nutrition.SugarGrams, x.Nutrition.SodiumMg, x.Nutrition.CholesterolMg);
        var ingredients = x.Ingredients.OrderBy(i => i.DisplayOrder).Select(i => new AdminIngredientLinkRequest(i.IngredientId, i.Quantity, i.Unit, i.IsOptional, i.CanBeRemoved, i.CanBeReplaced, i.IsPrimaryIngredient, i.DisplayOrder)).ToArray();
        var allergens = x.Allergens.Select(a => new AdminAllergenLinkRequest(a.AllergenId, a.AllergenLevel)).ToArray();
        var prices = x.Prices.Select(p => new AdminPriceRequest(p.PriceType, p.CurrencyCode.Trim(), p.Amount, p.EffectiveFrom, p.EffectiveUntil, p.IsActive)).ToArray();
        var media = x.Media
            .Where(m => m.Status == "ACTIVE")
            .OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => m.DisplayOrder)
            .Select(m => new AdminMediaResponse(
                m.Id,
                x.Id,
                m.MediaType,
                m.ObjectKey,
                m.PublicUrl,
                m.MimeType ?? "application/octet-stream",
                m.IsPrimary,
                m.DisplayOrder,
                m.Status,
                m.Translations.Where(t => t.LanguageCode == "en").Select(t => t.AltText).FirstOrDefault(),
                m.ThumbnailObjectKey,
                m.ThumbnailUrl))
            .ToArray();
        return new(x.Id, x.Status, new(x.Sku, x.CategoryId, x.PreparationTimeMinutes, x.IsVegetarian, x.IsVegan, x.IsGlutenFree, x.IsDairyFree, x.IsAvailable, x.AvailableFrom, x.AvailableUntil, translations, nutrition, ingredients, allergens, prices, x.Status, x.IsSpicy, x.SpiceLevel), media);
    }

    public async Task<Guid> CreateMealAsync(UpsertMealRequest request, Guid? userId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); var now = clock.GetUtcNow();
        var meal = new MealItem { Sku = request.Sku, CategoryId = request.CategoryId, PreparationTimeMinutes = request.PreparationTimeMinutes, IsVegetarian = request.IsVegetarian, IsVegan = request.IsVegan, IsGlutenFree = request.IsGlutenFree, IsDairyFree = request.IsDairyFree, IsSpicy = request.IsSpicy ?? false, SpiceLevel = request.SpiceLevel ?? 0, IsAvailable = request.IsAvailable, AvailableFrom = request.AvailableFrom, AvailableUntil = request.AvailableUntil, Status = request.Status?.Trim().ToUpperInvariant() ?? "DRAFT", CreatedAt = now, UpdatedAt = now, CreatedBy = userId, UpdatedBy = userId, RowVersion = 1 };
        ApplyTranslations(meal, request.Translations, now); ApplyNutrition(meal, request.Nutrition, now); ApplyAggregate(meal, request, now, userId); db.MealItems.Add(meal); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return meal.Id;
    }

    public async Task<bool> UpdateMealAsync(Guid mealId, UpsertMealRequest request, Guid? userId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var meal = await db.MealItems.Include(x => x.Translations).Include(x => x.Nutrition).Include(x => x.Ingredients).Include(x => x.Allergens).Include(x => x.Prices).SingleOrDefaultAsync(x => x.Id == mealId, ct); 
        if (meal is null) 
            return false; 
        var now = clock.GetUtcNow();
        meal.Sku = request.Sku; 
        meal.CategoryId = request.CategoryId;
        meal.PreparationTimeMinutes = request.PreparationTimeMinutes; 
        meal.IsVegetarian = request.IsVegetarian; 
        meal.IsVegan = request.IsVegan;
        meal.IsGlutenFree = request.IsGlutenFree; 
        meal.IsDairyFree = request.IsDairyFree; 
        if (request.IsSpicy.HasValue)
            meal.IsSpicy = request.IsSpicy.Value;
        if (request.SpiceLevel.HasValue)
            meal.SpiceLevel = request.SpiceLevel.Value;
        else if (request.IsSpicy == false)
            meal.SpiceLevel = 0;
        meal.IsAvailable = request.IsAvailable; 
        meal.AvailableFrom = request.AvailableFrom; 
        meal.AvailableUntil = request.AvailableUntil; 
        if (request.Status is not null) 
            meal.Status = request.Status.Trim().ToUpperInvariant(); 
        meal.UpdatedAt = now; 
        meal.UpdatedBy = userId; 
        meal.RowVersion++;
        db.MealItemTranslations.RemoveRange(meal.Translations); 
        db.MealItemIngredients.RemoveRange(meal.Ingredients); 
        db.MealItemAllergens.RemoveRange(meal.Allergens); 
        db.MealPrices.RemoveRange(meal.Prices); 
        meal.Translations.Clear(); meal.Ingredients.Clear();
        meal.Allergens.Clear(); meal.Prices.Clear(); ApplyTranslations(meal, request.Translations, now); ApplyNutrition(meal, request.Nutrition, now); ApplyAggregate(meal, request, now, userId); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return true;
    }

    public async Task<bool> SetMealStatusAsync(Guid mealId, string status, Guid? userId, CancellationToken ct) { if (status is not ("DRAFT" or "ACTIVE" or "INACTIVE" or "ARCHIVED")) throw new ArgumentException("Invalid meal status."); var meal = await db.MealItems.SingleOrDefaultAsync(x => x.Id == mealId, ct); if (meal is null) return false; meal.Status = status; meal.UpdatedBy = userId; meal.UpdatedAt = clock.GetUtcNow(); meal.RowVersion++; await db.SaveChangesAsync(ct); return true; }
    public async Task<AdminMediaResponse?> AddMediaAsync(Guid mealId, SaveMediaRequest request, Guid? userId, CancellationToken ct)
    {
        if (!await db.MealItems.AnyAsync(x => x.Id == mealId, ct)) return null;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (request.IsPrimary)
            await db.MealMedia.Where(x => x.MealItemId == mealId && x.IsPrimary)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsPrimary, false), ct);

        var now = clock.GetUtcNow();
        var media = new MealMedia
        {
            MealItemId = mealId,
            MediaType = request.MediaType,
            ObjectKey = request.ObjectKey,
            PublicUrl = request.PublicUrl,
            MimeType = request.ContentType,
            IsPrimary = request.IsPrimary,
            DisplayOrder = request.DisplayOrder,
            Status = "ACTIVE",
            StorageProvider = "S3",
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = userId
        };
        if (!string.IsNullOrWhiteSpace(request.AltTextEn))
            media.Translations.Add(new() { LanguageCode = "en", AltText = request.AltTextEn.Trim(), CreatedAt = now, UpdatedAt = now });

        db.Add(media);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(media.Id, mealId, media.MediaType, media.ObjectKey, media.PublicUrl, request.ContentType, media.IsPrimary, media.DisplayOrder, media.Status, request.AltTextEn?.Trim(), media.ThumbnailObjectKey, media.ThumbnailUrl);
    }

    public async Task<AdminThumbnailUpdateResponse?> SetThumbnailAsync(Guid mealId, SaveThumbnailRequest request, CancellationToken ct)
    {
        var media = await db.MealMedia
            .Include(m => m.Translations)
            .Where(m => m.MealItemId == mealId && m.Status == "ACTIVE" && m.MediaType == "IMAGE")
            .OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => m.DisplayOrder)
            .FirstOrDefaultAsync(ct);
        if (media is null) return null;

        var previousObjectKey = media.ThumbnailObjectKey;
        media.ThumbnailObjectKey = request.ObjectKey;
        media.ThumbnailUrl = request.PublicUrl;
        media.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        var altTextEn = media.Translations
            .Where(t => t.LanguageCode == "en")
            .Select(t => t.AltText)
            .FirstOrDefault();
        var response = new AdminMediaResponse(media.Id, mealId, media.MediaType, media.ObjectKey, media.PublicUrl, media.MimeType ?? "application/octet-stream", media.IsPrimary, media.DisplayOrder, media.Status, altTextEn, media.ThumbnailObjectKey, media.ThumbnailUrl);
        return new(response, previousObjectKey);
    }
    public async Task<bool> DeleteMediaAsync(Guid mealId, Guid mediaId, CancellationToken ct) => await db.MealMedia.Where(x => x.Id == mediaId && x.MealItemId == mealId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "DELETED").SetProperty(x => x.IsPrimary, false), ct) > 0;

    public async Task<PagedResult<AdminMealPlanSummaryResponse>> GetMealPlansAsync(string? search, int page, int pageSize, CancellationToken ct)
    {
        var query = db.MealPlanTemplates.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.Translations.Any(t => t.Name.Contains(term)));
        }
        var count = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(x => x.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new AdminMealPlanSummaryResponse(
                x.Id,
                x.Code,
                x.Translations.Where(t => t.LanguageCode == "en").Select(t => t.Name).FirstOrDefault()
                    ?? x.Translations.Select(t => t.Name).FirstOrDefault()
                    ?? x.Code,
                x.PlanType,
                x.DurationDays,
                x.IsCustomizable,
                x.IsPublished,
                x.IsActive,
                x.ValidFrom,
                x.ValidUntil,
                x.UpdatedAt))
            .ToListAsync(ct);

        return new(rows, new(page, pageSize, count, (int)Math.Ceiling(count / (double)pageSize)));
    }

    public async Task<AdminMealPlanDetailResponse?> GetMealPlanAsync(Guid planId, CancellationToken ct)
    {
        var plan = await db.MealPlanTemplates.AsNoTracking()
            .Include(x => x.Translations)
            .Include(x => x.Days).ThenInclude(x => x.Slots).ThenInclude(x => x.MealType).ThenInclude(x => x.Translations)
            .Include(x => x.Days).ThenInclude(x => x.Slots).ThenInclude(x => x.Options).ThenInclude(x => x.MealItem).ThenInclude(x => x.Translations)
            .SingleOrDefaultAsync(x => x.Id == planId, ct);
        if (plan is null) return null;

        return new(
            plan.Id,
            plan.Code,
            plan.PlanType,
            plan.DurationDays,
            plan.IsCustomizable,
            plan.IsPublished,
            plan.IsActive,
            plan.ValidFrom,
            plan.ValidUntil,
            plan.Translations.Select(x => new AdminPlanTranslationResponse(x.LanguageCode, x.Name, x.ShortDescription, x.FullDescription)).ToArray(),
            plan.Days.OrderBy(x => x.DisplayOrder).Select(day => new AdminPlanDayResponse(
                day.Id,
                plan.Id,
                day.MenuWeekday,
                day.DisplayOrder,
                day.IsActive,
                day.Slots.Count,
                day.Slots.OrderBy(x => x.DisplayOrder).Select(slot => new AdminPlanSlotResponse(
                    slot.Id,
                    slot.MealTypeId,
                    slot.MealType.Translations.FirstOrDefault(x => x.LanguageCode == "en")?.Name ?? slot.MealType.Code,
                    slot.DisplayOrder,
                    slot.MinimumSelection,
                    slot.MaximumSelection,
                    slot.IsRequired,
                    slot.Options.OrderBy(x => x.DisplayOrder).Select(option => new AdminPlanOptionResponse(
                        option.Id,
                        option.MealItemId,
                        option.MealItem.Translations.FirstOrDefault(x => x.LanguageCode == "en")?.Name ?? option.MealItem.Sku,
                        option.IsDefault)).ToArray())).ToArray())).ToArray());
    }

    public async Task<Guid> CreatePlanAsync(CreatePlanRequest request, Guid? userId, CancellationToken ct) { var now = clock.GetUtcNow(); ValidatePlanDays(request.Days ?? []); var p = new MealPlanTemplate { Code = request.Code, PlanType = request.PlanType, DurationDays = request.DurationDays, IsCustomizable = request.IsCustomizable, IsActive = true, IsPublished = false, ValidFrom = request.ValidFrom, ValidUntil = request.ValidUntil, CreatedAt = now, UpdatedAt = now, CreatedBy = userId, UpdatedBy = userId, RowVersion = 1 }; foreach (var t in request.Translations) p.Translations.Add(new() { LanguageCode = t.LanguageCode, Name = t.Name, ShortDescription = t.ShortDescription, FullDescription = t.FullDescription, CreatedAt = now, UpdatedAt = now }); AddPlanStructure(p, request.Days ?? [], now, userId); db.Add(p); await db.SaveChangesAsync(ct); return p.Id; }
    public async Task<bool> UpdatePlanAsync(Guid planId, CreatePlanRequest request, Guid? userId, CancellationToken ct) { await using var tx = await db.Database.BeginTransactionAsync(ct); var p = await db.MealPlanTemplates.Include(x => x.Translations).SingleOrDefaultAsync(x => x.Id == planId, ct); if (p is null) return false; if (request.Days is not null) ValidatePlanDays(request.Days); var now = clock.GetUtcNow(); p.Code = request.Code; p.PlanType = request.PlanType; p.DurationDays = request.DurationDays; p.IsCustomizable = request.IsCustomizable; p.ValidFrom = request.ValidFrom; p.ValidUntil = request.ValidUntil; p.UpdatedAt = now; p.UpdatedBy = userId; p.RowVersion++; db.MealPlanTemplateTranslations.RemoveRange(p.Translations); foreach (var t in request.Translations) p.Translations.Add(new() { LanguageCode = t.LanguageCode, Name = t.Name, ShortDescription = t.ShortDescription, FullDescription = t.FullDescription, CreatedAt = now, UpdatedAt = now }); if (request.Days is not null) { await db.MealPlanTemplateDays.Where(x => x.MealPlanTemplateId == planId).ExecuteDeleteAsync(ct); AddPlanStructure(p, request.Days, now, userId); } await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return true; }
    public async Task<bool> DeletePlanAsync(Guid planId, CancellationToken ct) { await using var tx = await db.Database.BeginTransactionAsync(ct); await db.MealPlanPrices.Where(x => x.MealPlanTemplateId == planId).ExecuteDeleteAsync(ct); var deleted = await db.MealPlanTemplates.Where(x => x.Id == planId).ExecuteDeleteAsync(ct) > 0; await tx.CommitAsync(ct); return deleted; }
    public async Task<IReadOnlyList<MealPlanTemplateDayResponse>?> GetTemplateDaysAsync(Guid templateId, CancellationToken ct)
    {
        if (!await db.MealPlanTemplates.AnyAsync(x => x.Id == templateId, ct)) return null;
        return await db.MealPlanTemplateDays.AsNoTracking()
            .Where(x => x.MealPlanTemplateId == templateId)
            .OrderBy(x => x.DisplayOrder)
            .ThenBy(x => x.MenuWeekday)
            .Select(x => new MealPlanTemplateDayResponse(x.Id, templateId, x.MenuWeekday, x.DisplayOrder, x.IsActive, x.Slots.Count))
            .ToListAsync(ct);
    }

    public async Task<Guid?> CreateTemplateDayAsync(Guid templateId, UpsertMealPlanTemplateDayRequest request, Guid? userId, CancellationToken ct)
    {
        if (!await db.MealPlanTemplates.AnyAsync(x => x.Id == templateId, ct)) return null;
        var weekday = RequireDeliveryWeekday(request.MenuWeekday);
        if (await db.MealPlanTemplateDays.AnyAsync(x => x.MealPlanTemplateId == templateId && x.MenuWeekday == weekday, ct))
            throw DuplicateWeekday(weekday);
        var now = clock.GetUtcNow();
        var day = new MealPlanTemplateDay { MealPlanTemplateId = templateId, MenuWeekday = weekday, DisplayOrder = request.DisplayOrder, IsActive = request.IsActive, CreatedAt = now, UpdatedAt = now, CreatedBy = userId, UpdatedBy = userId };
        db.Add(day);
        await db.SaveChangesAsync(ct);
        return day.Id;
    }

    public async Task<bool> UpdateTemplateDayAsync(Guid templateId, Guid dayId, UpsertMealPlanTemplateDayRequest request, Guid? userId, CancellationToken ct)
    {
        var day = await db.MealPlanTemplateDays.SingleOrDefaultAsync(x => x.Id == dayId && x.MealPlanTemplateId == templateId, ct);
        if (day is null) return false;
        var weekday = RequireDeliveryWeekday(request.MenuWeekday);
        if (await db.MealPlanTemplateDays.AnyAsync(x => x.MealPlanTemplateId == templateId && x.Id != dayId && x.MenuWeekday == weekday, ct))
            throw DuplicateWeekday(weekday);
        day.MenuWeekday = weekday;
        day.DisplayOrder = request.DisplayOrder;
        day.IsActive = request.IsActive;
        day.UpdatedAt = clock.GetUtcNow();
        day.UpdatedBy = userId;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeactivateTemplateDayAsync(Guid templateId, Guid dayId, Guid? userId, CancellationToken ct)
    {
        var day = await db.MealPlanTemplateDays.SingleOrDefaultAsync(x => x.Id == dayId && x.MealPlanTemplateId == templateId, ct);
        if (day is null) return false;
        day.IsActive = false;
        day.UpdatedAt = clock.GetUtcNow();
        day.UpdatedBy = userId;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<MealPlanTemplateDayDetailResponse?> GetTemplateDayByWeekdayAsync(Guid templateId, MenuWeekday weekday, CancellationToken ct)
    {
        var day = await db.MealPlanTemplateDays.AsNoTracking()
            .Include(x => x.Slots).ThenInclude(x => x.MealType).ThenInclude(x => x.Translations)
            .Include(x => x.Slots).ThenInclude(x => x.Options).ThenInclude(x => x.MealItem).ThenInclude(x => x.Translations)
            .SingleOrDefaultAsync(x => x.MealPlanTemplateId == templateId && x.MenuWeekday == weekday && x.IsActive, ct);
        return day is null ? null : ToTemplateDayDetail(day);
    }
    public async Task<Guid?> AddPlanSlotAsync(Guid dayId, CreatePlanSlotRequest request, Guid? userId, CancellationToken ct) { if (!await db.MealPlanTemplateDays.AnyAsync(x => x.Id == dayId, ct)) return null; var now = clock.GetUtcNow(); var s = new MealPlanTemplateSlot { MealPlanTemplateDayId = dayId, MealTypeId = request.MealTypeId, DisplayOrder = request.DisplayOrder, MinimumSelection = request.MinimumSelection, MaximumSelection = request.MaximumSelection, IsRequired = request.IsRequired, SelectionCutoffTime = request.SelectionCutoffTime, AllowsPaidUpgrade = request.AllowsPaidUpgrade, IsActive = true, CreatedAt = now, UpdatedAt = now, CreatedBy = userId, UpdatedBy = userId, RowVersion = 1 }; db.Add(s); await db.SaveChangesAsync(ct); return s.Id; }
    public async Task<Guid?> AddSlotOptionAsync(Guid slotId, CreateSlotOptionRequest request, Guid? userId, CancellationToken ct) { if (!await db.MealPlanTemplateSlots.AnyAsync(x => x.Id == slotId, ct)) return null; if (request.IsDefault) await db.MealPlanSlotOptions.Where(x => x.MealPlanTemplateSlotId == slotId && x.IsDefault).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsDefault, false), ct); var now = clock.GetUtcNow(); var o = new MealPlanSlotOption { MealPlanTemplateSlotId = slotId, MealItemId = request.MealItemId, AdditionalPrice = request.AdditionalPrice, IsDefault = request.IsDefault, IsAvailable = request.IsAvailable, DisplayOrder = request.DisplayOrder, CreatedAt = now, UpdatedAt = now, CreatedBy = userId, UpdatedBy = userId }; db.Add(o); await db.SaveChangesAsync(ct); return o.Id; }
    public async Task<bool> DeleteSlotOptionAsync(Guid slotId, Guid optionId, CancellationToken ct) => await db.MealPlanSlotOptions.Where(x => x.Id == optionId && x.MealPlanTemplateSlotId == slotId).ExecuteDeleteAsync(ct) > 0;
    public async Task<bool> SetPlanPublishedAsync(Guid planId, bool published, Guid? userId, CancellationToken ct) { var p = await db.MealPlanTemplates.Include(x => x.Translations).Include(x => x.Days).ThenInclude(x => x.Slots).ThenInclude(x => x.Options).SingleOrDefaultAsync(x => x.Id == planId, ct); if (p is null) return false; if (published && (p.Translations.Count == 0 || p.Days.Count == 0 || p.Days.Any(d => d.Slots.Count == 0 || d.Slots.Any(s => s.Options.Count == 0)))) throw new InvalidOperationException("A plan requires translations, days, slots, and options before publication."); p.IsPublished = published; p.UpdatedAt = clock.GetUtcNow(); p.UpdatedBy = userId; p.RowVersion++; await db.SaveChangesAsync(ct); return true; }

    private static void AddPlanStructure(MealPlanTemplate plan, IEnumerable<UpsertPlanDayRequest> days, DateTimeOffset now, Guid? userId)
    {
        foreach (var requestDay in days)
        {
            var weekday = ResolveWeekday(requestDay);
            var day = new MealPlanTemplateDay { MenuWeekday = weekday, DisplayOrder = requestDay.DisplayOrder, IsActive = requestDay.IsActive, CreatedAt = now, UpdatedAt = now, CreatedBy = userId, UpdatedBy = userId };
            foreach (var requestSlot in requestDay.Slots)
            {
                var slot = new MealPlanTemplateSlot { MealTypeId = requestSlot.MealTypeId, DisplayOrder = requestSlot.DisplayOrder, MinimumSelection = requestSlot.MinimumSelection, MaximumSelection = requestSlot.MaximumSelection, IsRequired = requestSlot.IsRequired, SelectionCutoffTime = requestSlot.SelectionCutoffTime, AllowsPaidUpgrade = requestSlot.AllowsPaidUpgrade, IsActive = true, CreatedAt = now, UpdatedAt = now, CreatedBy = userId, UpdatedBy = userId, RowVersion = 1 };
                foreach (var requestOption in requestSlot.Options) slot.Options.Add(new() { MealItemId = requestOption.MealItemId, AdditionalPrice = requestOption.AdditionalPrice, IsDefault = requestOption.IsDefault, IsAvailable = requestOption.IsAvailable, DisplayOrder = requestOption.DisplayOrder, CreatedAt = now, UpdatedAt = now, CreatedBy = userId, UpdatedBy = userId });
                day.Slots.Add(slot);
            }
            plan.Days.Add(day);
        }
    }

    private void ValidatePlanDays(IEnumerable<UpsertPlanDayRequest> days)
    {
        var requests = days.ToArray();
        if (requests.Any(x => x.DisplayOrder <= 0))
            throw new TemplateDayException("INVALID_DISPLAY_ORDER", "Display order must be greater than zero.", 400);
        var resolved = requests.Select(ResolveWeekday).ToArray();
        var duplicate = resolved.GroupBy(x => x).FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null) throw DuplicateWeekday(duplicate.Key);
        foreach (var weekday in resolved) RequireDeliveryWeekday(weekday);
    }

    private MenuWeekday RequireDeliveryWeekday(MenuWeekday? weekday)
    {
        if (weekday is null) throw new TemplateDayException("MENU_WEEKDAY_REQUIRED", "Menu weekday is required.", 400);
        if (!deliverySchedule.Value.IsDeliveryDay(weekday.Value))
            throw new TemplateDayException("NON_DELIVERY_WEEKDAY", $"{weekday.Value} is configured as a non-delivery day.", 422);
        return weekday.Value;
    }

    private static TemplateDayException DuplicateWeekday(MenuWeekday weekday) => new(
        "DUPLICATE_TEMPLATE_WEEKDAY",
        $"{weekday} already exists in this template.",
        409);

    private static MenuWeekday ResolveWeekday(UpsertPlanDayRequest request) => request.MenuWeekday
        ?? throw new TemplateDayException("MENU_WEEKDAY_REQUIRED", "Menu weekday is required.", 400);

    private static MealPlanTemplateDayDetailResponse ToTemplateDayDetail(MealPlanTemplateDay day) => new(
        day.Id,
        day.MealPlanTemplateId,
        day.MenuWeekday,
        day.DisplayOrder,
        day.IsActive,
        day.Slots.Count,
        day.Slots.OrderBy(x => x.DisplayOrder).Select(slot => new TemplateMenuSlotResponse(
            slot.Id,
            slot.MealTypeId,
            slot.MealType.Code,
            slot.MealType.Translations.FirstOrDefault(x => x.LanguageCode == "en")?.Name ?? slot.MealType.Code,
            slot.DisplayOrder,
            slot.MinimumSelection,
            slot.MaximumSelection,
            slot.IsRequired,
            slot.SelectionCutoffTime,
            slot.AllowsPaidUpgrade,
            slot.IsActive,
            slot.Options.OrderBy(x => x.DisplayOrder).Select(option => new TemplateMenuOptionResponse(
                option.Id,
                option.MealItemId,
                option.MealItem.Translations.FirstOrDefault(x => x.LanguageCode == "en")?.Name ?? option.MealItem.Sku,
                option.AdditionalPrice,
                option.IsDefault,
                option.IsAvailable,
                option.DisplayOrder)).ToArray())).ToArray());

    private static void ApplyTranslations(MealItem meal, IEnumerable<AdminTranslationRequest> values, DateTimeOffset now) { foreach (var t in values) meal.Translations.Add(new() { LanguageCode = t.LanguageCode, Name = t.Name, ShortDescription = t.ShortDescription, FullDescription = t.FullDescription, PreparationInstructions = t.PreparationInstructions, ServingNotes = t.ServingNotes, CreatedAt = now, UpdatedAt = now }); }
    private static void ApplyNutrition(MealItem meal, AdminNutritionRequest? n, DateTimeOffset now) { if (n is null) { meal.Nutrition = null; return; } meal.Nutrition ??= new() { CreatedAt = now }; meal.Nutrition.ServingQuantity = n.ServingQuantity; meal.Nutrition.ServingUnit = n.ServingUnit; meal.Nutrition.CaloriesKcal = n.CaloriesKcal; meal.Nutrition.ProteinGrams = n.ProteinGrams; meal.Nutrition.CarbohydratesGrams = n.CarbohydratesGrams; meal.Nutrition.FatGrams = n.FatGrams; meal.Nutrition.SaturatedFatGrams = n.SaturatedFatGrams; meal.Nutrition.TransFatGrams = n.TransFatGrams; meal.Nutrition.FiberGrams = n.FiberGrams; meal.Nutrition.SugarGrams = n.SugarGrams; meal.Nutrition.SodiumMg = n.SodiumMg; meal.Nutrition.CholesterolMg = n.CholesterolMg; meal.Nutrition.UpdatedAt = now; }
    private static void ApplyAggregate(MealItem meal, UpsertMealRequest request, DateTimeOffset now, Guid? userId)
    {
        foreach (var x in request.Ingredients ?? []) meal.Ingredients.Add(new() { IngredientId = x.IngredientId, Quantity = x.Quantity, Unit = x.Unit, IsOptional = x.IsOptional, CanBeRemoved = x.CanBeRemoved, CanBeReplaced = x.CanBeReplaced, IsPrimaryIngredient = x.IsPrimaryIngredient, DisplayOrder = x.DisplayOrder, CreatedAt = now });
        foreach (var x in request.Allergens ?? []) meal.Allergens.Add(new() { AllergenId = x.AllergenId, AllergenLevel = x.Level, CreatedAt = now });
        foreach (var x in request.Prices ?? []) meal.Prices.Add(new() { PriceType = x.PriceType, CurrencyCode = x.CurrencyCode, Amount = x.Amount, EffectiveFrom = x.EffectiveFrom, EffectiveUntil = x.EffectiveUntil, IsActive = x.IsActive, CreatedAt = now, UpdatedAt = now, CreatedBy = userId, UpdatedBy = userId });
    }
}
