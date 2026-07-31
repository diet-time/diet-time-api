using DietTime.Contracts;
using DietTime.Domain;

namespace DietTime.Application;

public enum AdminWriteResult { Success, NotFound, Conflict }

public interface IMealQueryService
{
    Task<IReadOnlyList<PlanCategoryResponse>> GetPlanCategoriesAsync(string language, DateOnly today, CancellationToken cancellationToken);
    Task<MealPlanResponse?> GetPlanAsync(Guid planId, string language, DateOnly today, CancellationToken cancellationToken);
    Task<IReadOnlyList<CalendarDayResponse>?> GetCalendarAsync(Guid planId, DateOnly startDate, int numberOfDays, string language, CancellationToken cancellationToken);
    Task<PagedResult<MealCardResponse>?> GetPlanMealsAsync(Guid planId, MealListQuery query, string language, DateTimeOffset now, CancellationToken cancellationToken);
    Task<MealDetailsResponse?> GetMealAsync(Guid mealId, string language, DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<MealTypeResponse>> GetMealTypesAsync(string language, CancellationToken cancellationToken);
    Task<PagedResult<MealSearchResponse>> SearchMealsAsync(MealSearchQuery query, string language, DateTimeOffset now, CancellationToken cancellationToken);
}

public interface IGuestHomeService
{
    Task<GuestHomeResponse?> GetAsync(GuestHomeQuery query, DateTimeOffset now, CancellationToken cancellationToken);
    Task<GuestMenuResponse?> GetMenuAsync(string planCode, GuestMenuQuery query, DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<GuestAllergenLookupResponse>> GetAllergensAsync(string language, CancellationToken cancellationToken);
}

public interface IMealSelectionService
{
    Task<MealSelectionValidationResponse> ValidateAsync(MealSelectionRequest request, DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed record CustomerProfileUpsertResult(
    CustomerProfileResponse? Profile,
    IReadOnlyList<Guid> InvalidAllergenIds)
{
    public bool IsSuccess => Profile is not null && InvalidAllergenIds.Count == 0;
}

public interface ICustomerProfileService
{
    Task<CustomerProfileResponse?> GetAsync(Guid userId, CancellationToken cancellationToken);
    Task<CustomerProfileUpsertResult> UpsertAsync(Guid userId, UpsertCustomerProfileRequest request, CancellationToken cancellationToken);
    Task<CustomerProfileResponse> UpdatePreferredNameAsync(Guid userId, string preferredName, CancellationToken cancellationToken);
}

public sealed record CustomerNutritionCalculationInput(
    string? GenderCode,
    DateOnly? DateOfBirth,
    decimal? HeightCm,
    decimal? WeightKg,
    string? GoalCode,
    string? ActivityLevelCode,
    DateOnly CalculationDate);
public sealed record CustomerNutritionCalculationResult(
    int? DailyCaloriesKcal,
    decimal? DailyProteinG,
    decimal? DailyCarbohydratesG,
    decimal? DailyFatG,
    decimal? DailyFiberG,
    int? DailyWaterMl,
    string? CalculationMethod,
    string? CalculationVersion);

public interface ICustomerNutritionCalculator
{
    CustomerNutritionCalculationResult? Calculate(CustomerNutritionCalculationInput input);
}

public sealed class GuestProfileOptions
{
    public const string SectionName = "GuestProfile";
    public int TokenExpiryDays { get; set; } = 30;
    public int TokenLengthBytes { get; set; } = 32;
    public string HashAlgorithm { get; set; } = "SHA256";
    public int ExpiredProfileRetentionDays { get; set; } = 7;
    public int CleanupIntervalHours { get; set; } = 24;
    public int CleanupBatchSize { get; set; } = 500;
}

public sealed record GuestSessionTokenResult(string RawToken, DateTimeOffset ExpiresAt);
public interface IGuestTokenGenerator
{
    GuestSessionTokenResult Generate();
}
public interface IGuestTokenHasher
{
    bool IsValidFormat(string? rawToken);
    string Hash(string rawToken);
    bool Verify(string rawToken, string storedHash);
}

public enum GuestTokenResolutionStatus
{
    Valid,
    ProfileNotFound,
    Invalid
}
public sealed record GuestTokenResolution(
    GuestTokenResolutionStatus Status,
    string? TokenHash,
    Guid? ProfileId);
public interface IGuestTokenResolver
{
    Task<GuestTokenResolution> ResolveAsync(
        IReadOnlyList<string> tokenHeaders,
        bool requireProfile,
        CancellationToken cancellationToken);
}

public sealed record GuestProfileUpsertResult(
    GuestOnboardingProfileResponse? Profile,
    IReadOnlyList<Guid> InvalidAllergenIds);
public interface IGuestProfileService
{
    Task<GuestOnboardingProfileResponse?> GetAsync(Guid profileId, CancellationToken cancellationToken);
    Task<GuestProfileUpsertResult> UpsertAsync(string tokenHash, UpsertGuestProfileRequest request, CancellationToken cancellationToken);
}
public sealed record GuestOnboardingProgressInput(
    string? GenderCode,
    DateOnly? DateOfBirth,
    decimal? HeightCm,
    decimal? WeightKg,
    string? GoalCode,
    string? DailyRoutineCode,
    string? ActivityLevelCode,
    bool AllergensConfirmed,
    bool PreferencesConfirmed);
public sealed record GuestOnboardingProgressResult(
    string NextStepCode,
    int CompletionPercentage,
    bool ShouldShowOnboarding);
public interface IGuestOnboardingProgressResolver
{
    GuestOnboardingProgressResult Resolve(GuestOnboardingProgressInput input);
}
public interface IGuestPlanRecommendationService
{
    Task<IReadOnlyList<GuestPlanRecommendationResponse>> GetAsync(
        Guid profileId,
        CancellationToken cancellationToken);
}
public interface IGuestProfileCleanupService
{
    Task<int> DeleteExpiredBatchAsync(CancellationToken cancellationToken);
}
public sealed class InvalidGuestSessionException()
    : Exception("Guest session is invalid or has expired.");

public interface IAdminMealService
{
    Task<AdminDashboardResponse> GetDashboardAsync(CancellationToken cancellationToken);
    Task<PagedResult<AdminAllergenResponse>> GetAllergensAsync(string? search, string? sort, int page, int pageSize, CancellationToken cancellationToken);
    Task<Guid?> CreateAllergenAsync(UpsertAllergenRequest request, Guid? userId, CancellationToken cancellationToken);
    Task<AdminWriteResult> UpdateAllergenAsync(Guid allergenId, UpsertAllergenRequest request, Guid? userId, CancellationToken cancellationToken);
    Task<PagedResult<AdminIngredientResponse>> GetIngredientsAsync(string? search, string? sort, int page, int pageSize, CancellationToken cancellationToken);
    Task<Guid?> CreateIngredientAsync(UpsertIngredientRequest request, Guid? userId, CancellationToken cancellationToken);
    Task<AdminWriteResult> UpdateIngredientAsync(Guid ingredientId, UpsertIngredientRequest request, Guid? userId, CancellationToken cancellationToken);
    Task<PagedResult<AdminMealCategoryResponse>> GetMealCategoriesAsync(string? search, string? sort, int page, int pageSize, CancellationToken cancellationToken);
    Task<Guid?> CreateMealCategoryAsync(UpsertMealCategoryRequest request, Guid? userId, CancellationToken cancellationToken);
    Task<AdminWriteResult> UpdateMealCategoryAsync(Guid categoryId, UpsertMealCategoryRequest request, Guid? userId, CancellationToken cancellationToken);
    Task<PagedResult<AdminMealTypeResponse>> GetMealTypesAsync(string? search, string? sort, int page, int pageSize, CancellationToken cancellationToken);
    Task<AdminWriteResult> UpdateMealTypeAsync(Guid mealTypeId, UpsertMealTypeRequest request, Guid? userId, CancellationToken cancellationToken);
    Task<PagedResult<AdminMealSummaryResponse>> GetMealsAsync(string? search, int page, int pageSize, CancellationToken cancellationToken);
    Task<AdminMealResponse?> GetMealAsync(Guid mealId, CancellationToken cancellationToken);
    Task<Guid> CreateMealAsync(UpsertMealRequest request, Guid? userId, CancellationToken cancellationToken);
    Task<VersionedUpdateResponse?> UpdateMealAsync(Guid mealId, UpsertMealRequest request, Guid? userId, CancellationToken cancellationToken);
    Task<bool> SetMealStatusAsync(Guid mealId, string status, Guid? userId, CancellationToken cancellationToken);
    Task<AdminMediaResponse?> AddMediaAsync(Guid mealId, SaveMediaRequest request, Guid? userId, CancellationToken cancellationToken);
    Task<AdminThumbnailUpdateResponse?> SetThumbnailAsync(Guid mealId, SaveThumbnailRequest request, CancellationToken cancellationToken);
    Task<bool> DeleteMediaAsync(Guid mealId, Guid mediaId, CancellationToken cancellationToken);
    Task<PagedResult<AdminMealPlanSummaryResponse>> GetMealPlansAsync(string? search, int page, int pageSize, CancellationToken cancellationToken);
    Task<AdminMealPlanDetailResponse?> GetMealPlanAsync(Guid planId, CancellationToken cancellationToken);
    Task<Guid> CreatePlanAsync(CreatePlanRequest request, Guid? userId, CancellationToken cancellationToken);
    Task<VersionedUpdateResponse?> UpdatePlanAsync(Guid planId, CreatePlanRequest request, Guid? userId, CancellationToken cancellationToken);
    Task<AdminPlanImageResponse?> AddPlanImageAsync(Guid planId, SaveMediaRequest request, Guid? userId, CancellationToken cancellationToken);
    Task<bool> DeletePlanAsync(Guid planId, CancellationToken cancellationToken);
    Task<PagedResult<AdminMealPlanPriceResponse>> GetMealPlanPricesAsync(string? search, Guid? mealPlanTemplateId, string? status, string? currencyCode, int page, int pageSize, CancellationToken cancellationToken);
    Task<AdminMealPlanPriceResponse?> GetMealPlanPriceAsync(Guid priceId, CancellationToken cancellationToken);
    Task<AdminMealPlanPriceSummaryResponse> GetMealPlanPriceSummaryAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetMealPlanPriceCurrenciesAsync(CancellationToken cancellationToken);
    Task<Guid?> CreateMealPlanPriceAsync(UpsertMealPlanPriceRequest request, Guid? userId, CancellationToken cancellationToken);
    Task<AdminWriteResult> UpdateMealPlanPriceAsync(Guid priceId, UpsertMealPlanPriceRequest request, Guid? userId, CancellationToken cancellationToken);
    Task<AdminWriteResult> SetMealPlanPriceStatusAsync(Guid priceId, bool isActive, Guid? userId, CancellationToken cancellationToken);
    Task<AdminWriteResult> DeleteMealPlanPriceAsync(Guid priceId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MealPlanTemplateDayResponse>?> GetTemplateDaysAsync(Guid templateId, CancellationToken cancellationToken);
    Task<Guid?> CreateTemplateDayAsync(Guid templateId, UpsertMealPlanTemplateDayRequest request, Guid? userId, CancellationToken cancellationToken);
    Task<bool> UpdateTemplateDayAsync(Guid templateId, Guid dayId, UpsertMealPlanTemplateDayRequest request, Guid? userId, CancellationToken cancellationToken);
    Task<bool> DeactivateTemplateDayAsync(Guid templateId, Guid dayId, Guid? userId, CancellationToken cancellationToken);
    Task<MealPlanTemplateDayDetailResponse?> GetTemplateDayByWeekdayAsync(Guid templateId, MenuWeekday weekday, CancellationToken cancellationToken);
    Task<Guid?> AddPlanSlotAsync(Guid dayId, CreatePlanSlotRequest request, Guid? userId, CancellationToken cancellationToken);
    Task<Guid?> AddSlotOptionAsync(Guid slotId, CreateSlotOptionRequest request, Guid? userId, CancellationToken cancellationToken);
    Task<bool> DeleteSlotOptionAsync(Guid slotId, Guid optionId, CancellationToken cancellationToken);
}

public interface IStorageUrlService
{
    long MaxUploadSizeBytes { get; }
    string GetPublicUrl(string objectKey);
    string GetThumbnailUrl(string objectKey);
    Task UploadAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken);
    Task<StoredObject?> DownloadAsync(string objectKey, CancellationToken cancellationToken);
    Task DeleteAsync(string objectKey, CancellationToken cancellationToken);
}

public sealed record StoredObject(Stream Content, string ContentType, long? Length);

public interface ILanguageResolver { string Resolve(string? queryLanguage, string? acceptLanguage); }
public interface IOperationalCalendarService
{
    Task<bool> IsOperationalDateAsync(DateOnly date, CancellationToken cancellationToken);
}
public interface ITemplateMenuReader
{
    Task<MealPlanTemplateDayDetailResponse?> GetTemplateDayByWeekdayAsync(Guid templateId, MenuWeekday weekday, CancellationToken cancellationToken);
}
public interface IDeliverySchedulingService
{
    Task<DateOnly> GetNextDeliveryDateAsync(DateOnly joiningDate, IReadOnlyCollection<DayOfWeek> activeDeliveryDays, IReadOnlyCollection<DateOnly> holidays, CancellationToken cancellationToken);
    Task<MealPlanTemplateDayDetailResponse> GetMenuForDeliveryDateAsync(Guid templateId, DateOnly deliveryDate, CancellationToken cancellationToken);
}
public interface IAuthService
{
    Task<TokenResponse?> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken);
    Task<TokenResponse?> LoginAsync(LoginRequest request, CancellationToken cancellationToken);
    Task<TokenResponse?> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken);
}
