namespace DietTime.Contracts;

public sealed record ApiResponse<T>
{
    public T? Data { get; init; }
    public PaginationMeta? Meta { get; init; }
    public IReadOnlyList<ApiError> Errors { get; init; } = [];
    public static ApiResponse<T> Ok(T data, PaginationMeta? meta = null) => new() { Data = data, Meta = meta };
}
public sealed record ApiError(string Code, string Message, string? Field = null);
public sealed record PaginationMeta(int Page, int PageSize, int TotalCount, int TotalPages);
public sealed record PagedResult<T>(IReadOnlyList<T> Items, PaginationMeta Meta);

public sealed record PlanCategoryResponse(Guid Id, string Code, string Name, string? Description, string? ImageUrl, bool IsSelected);
public sealed record PlanPriceResponse(int DurationDays, int MealsPerDay, int SnacksPerDay, decimal Amount, string CurrencyCode);
public sealed record MealPlanResponse(Guid Id, string Code, string Name, string? Description, string PlanType, int DurationDays, bool IsCustomizable, IReadOnlyList<PlanPriceResponse> Prices, IReadOnlyList<MealTypeResponse> SupportedMealTypes);
public sealed record CalendarDayResponse(Guid TemplateDayId, DateOnly Date, int DayNumber, short DayOfWeek, string DayShortName, string DayName, bool IsAvailable);
public sealed record MealTypeResponse(Guid? Id, string Code, string Name, int DisplayOrder);
public sealed record MealCardResponse(Guid SlotOptionId, Guid SlotId, Guid MealItemId, MealTypeResponse MealType, string Name, string? ShortDescription, string? ThumbnailUrl, decimal? CaloriesKcal, decimal? ProteinGrams, decimal? CarbohydratesGrams, decimal? FatGrams, decimal AdditionalPrice, string CurrencyCode, bool IsDefault, bool IsAvailable, IReadOnlyList<string> AllergenCodes);
public sealed record MealSearchResponse(Guid MealItemId, string Sku, string Name, string? ShortDescription, string? ThumbnailUrl, decimal? CaloriesKcal, decimal? ProteinGrams, decimal? CarbohydratesGrams, decimal? FatGrams, decimal? Price, string? CurrencyCode, bool IsAvailable);

public sealed record CategoryResponse(Guid Id, string Code, string Name);
public sealed record NutritionResponse(decimal? ServingQuantity, string? ServingUnit, decimal? CaloriesKcal, decimal? ProteinGrams, decimal? CarbohydratesGrams, decimal? FatGrams, decimal? SaturatedFatGrams, decimal? TransFatGrams, decimal? FiberGrams, decimal? SugarGrams, decimal? SodiumMg, decimal? CholesterolMg);
public sealed record MediaResponse(Guid Id, string ImageUrl, string? ThumbnailUrl, string? AltText);
public sealed record IngredientResponse(Guid Id, string Name, decimal? Quantity, string? Unit, bool IsOptional, bool CanBeRemoved);
public sealed record AllergenResponse(Guid Id, string Code, string Name, string Level);
public sealed record MoneyResponse(decimal Amount, string CurrencyCode);
public sealed record MealDetailsResponse(Guid Id, string Sku, string Name, string? ShortDescription, string? FullDescription, CategoryResponse Category, string? PrimaryImageUrl, IReadOnlyList<MediaResponse> GalleryImages, NutritionResponse? Nutrition, IReadOnlyList<IngredientResponse> Ingredients, IReadOnlyList<AllergenResponse> Allergens, MoneyResponse? IndividualPrice, int? PreparationTimeMinutes, bool IsVegetarian, bool IsVegan, bool IsGlutenFree, bool IsDairyFree, bool IsAvailable);

public sealed record MealListQuery(DateOnly? Date, Guid? TemplateDayId, string? MealType, Guid? CategoryId, string? Search, int Page = 1, int PageSize = 20);
public sealed record MealSearchQuery(string? Search, Guid? CategoryId, string? MealType, bool? IsVegetarian, bool? IsVegan, bool? IsGlutenFree, decimal? MinimumProtein, decimal? MaximumCalories, int Page = 1, int PageSize = 20);

public sealed record MealSelectionRequest(Guid PlanId, Guid TemplateDayId, IReadOnlyList<MealSelectionItemRequest> Selections);
public sealed record MealSelectionItemRequest(Guid SlotId, Guid SlotOptionId, Guid MealItemId);
public sealed record MealSelectionValidationResponse(bool IsValid, decimal TotalAdditionalPrice, string CurrencyCode, IReadOnlyList<string> Warnings);

public sealed record ChangeMealStatusRequest(string Status);
public sealed record AdminMealSummaryResponse(Guid Id, string Sku, string Status, bool IsAvailable, string Name, DateTimeOffset UpdatedAt);
public sealed record AdminMealResponse(Guid Id, string Status, UpsertMealRequest Meal);
public sealed record AdminAllergenResponse(
    Guid Id,
    string Code,
    string NameEn,
    string NameAr,
    bool IsActive,
    int UsageCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
public sealed record UpsertAllergenRequest(string Code, string NameEn, string NameAr, bool IsActive = true);
public sealed record AdminIngredientResponse(
    Guid Id,
    string Code,
    string NameEn,
    string NameAr,
    bool IsActive,
    int UsageCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
public sealed record UpsertIngredientRequest(string Code, string NameEn, string NameAr, bool IsActive = true);
public sealed record AdminMealCategoryResponse(
    Guid Id,
    string Code,
    string NameEn,
    string NameAr,
    string? DescriptionEn,
    string? DescriptionAr,
    int DisplayOrder,
    bool IsActive,
    int UsageCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
public sealed record UpsertMealCategoryRequest(
    string Code,
    string NameEn,
    string NameAr,
    string? DescriptionEn,
    string? DescriptionAr,
    int DisplayOrder,
    bool IsActive = true);
public sealed record UpsertMealTypeRequest(string Code, string NameEn, int DisplayOrder, bool IsActive = true);
public sealed record AdminMealTypeResponse(
    Guid Id,
    string Code,
    string NameEn,
    string NameAr,
    int DisplayOrder,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
public sealed record DashboardMetricResponse(string Name, int Value);
public sealed record AdminDashboardResponse(
    int ActiveMeals,
    int DraftMeals,
    int UnavailableMeals,
    int PublishedPlans,
    int DraftPlans,
    int ExpiringMeals,
    int ScheduledPriceChanges,
    int MissingImages,
    int MissingArabic,
    int MissingNutrition,
    IReadOnlyList<DashboardMetricResponse> MealsByCategory);
public sealed record AdminTranslationRequest(
    string LanguageCode,
    string Name,
    string? ShortDescription,
    string? FullDescription,
    string? PreparationInstructions = null,
    string? ServingNotes = null);
public sealed record AdminNutritionRequest(decimal? ServingQuantity, string? ServingUnit, decimal? CaloriesKcal, decimal? ProteinGrams, decimal? CarbohydratesGrams, decimal? FatGrams, decimal? SaturatedFatGrams, decimal? TransFatGrams, decimal? FiberGrams, decimal? SugarGrams, decimal? SodiumMg, decimal? CholesterolMg);
public sealed record AdminIngredientLinkRequest(Guid IngredientId, decimal? Quantity, string? Unit, bool IsOptional, bool CanBeRemoved, bool CanBeReplaced, bool IsPrimaryIngredient, int DisplayOrder);
public sealed record AdminAllergenLinkRequest(Guid AllergenId, string Level);
public sealed record AdminPriceRequest(string PriceType, string CurrencyCode, decimal Amount, DateTimeOffset EffectiveFrom, DateTimeOffset? EffectiveUntil, bool IsActive);
public sealed record UpsertMealRequest(string Sku, Guid CategoryId, int? PreparationTimeMinutes, bool IsVegetarian, bool IsVegan, bool IsGlutenFree, bool IsDairyFree, bool IsAvailable, DateTimeOffset? AvailableFrom, DateTimeOffset? AvailableUntil, IReadOnlyList<AdminTranslationRequest> Translations, AdminNutritionRequest? Nutrition, IReadOnlyList<AdminIngredientLinkRequest>? Ingredients = null, IReadOnlyList<AdminAllergenLinkRequest>? Allergens = null, IReadOnlyList<AdminPriceRequest>? Prices = null, string? Status = null, bool? IsSpicy = null, short? SpiceLevel = null);
public sealed record SaveMediaRequest(string ObjectKey, string? PublicUrl, string ContentType, string MediaType, bool IsPrimary, int DisplayOrder, string? AltTextEn);
public sealed record AdminMediaResponse(Guid Id, Guid MealItemId, string MediaType, string ObjectKey, string? PublicUrl, string ContentType, bool IsPrimary, int DisplayOrder, string Status, string? AltTextEn);
public sealed record AdminMealPlanSummaryResponse(
    Guid Id,
    string Code,
    string Name,
    string PlanType,
    int DurationDays,
    bool IsCustomizable,
    bool IsPublished,
    bool IsActive,
    DateOnly? ValidFrom,
    DateOnly? ValidUntil,
    DateTimeOffset UpdatedAt);
public sealed record CreatePlanRequest(string Code, string PlanType, int DurationDays, bool IsCustomizable, DateOnly? ValidFrom, DateOnly? ValidUntil, IReadOnlyList<AdminTranslationRequest> Translations);
public sealed record CreatePlanDayRequest(int DayNumber, short? DayOfWeek, string EnglishLabel, string? ArabicLabel);
public sealed record CreatePlanSlotRequest(Guid MealTypeId, int DisplayOrder, int MinimumSelection, int MaximumSelection, bool IsRequired, TimeOnly? SelectionCutoffTime, bool AllowsPaidUpgrade);
public sealed record CreateSlotOptionRequest(Guid MealItemId, decimal AdditionalPrice, bool IsDefault, bool IsAvailable, int DisplayOrder);
public sealed record RegisterRequest(string Email, string Password);
public sealed record LoginRequest(string Email, string Password);
public sealed record RefreshRequest(string RefreshToken);
public sealed record TokenResponse(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
