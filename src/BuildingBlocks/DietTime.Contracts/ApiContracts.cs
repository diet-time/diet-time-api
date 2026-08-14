using DietTime.Domain;

namespace DietTime.Contracts;

public sealed record ApiResponse<T>
{
    public T? Data { get; init; }
    public PaginationMeta? Meta { get; init; }
    public IReadOnlyList<ApiError> Errors { get; init; } = [];
    public static ApiResponse<T> Ok(T data, PaginationMeta? meta = null) => new() { Data = data, Meta = meta };
}
public sealed record ApiError(string Code, string Message, string? Field = null);
public sealed record SendTwilioWhatsAppTemplateRequest(
    string To,
    string ContentSid,
    IReadOnlyDictionary<string, string> ContentVariables);
public sealed record SendWhatsAppTemplateResponse(bool Success, string? MessageId);
public sealed record PaginationMeta(int Page, int PageSize, int TotalCount, int TotalPages);
public sealed record PagedResult<T>(IReadOnlyList<T> Items, PaginationMeta Meta);

public sealed record PlanCategoryResponse(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    string? ImageUrl,
    bool IsSelected,
    decimal? DailyCaloriesKcal,
    decimal? StartingPrice,
    string? CurrencyCode,
    int? PriceDurationDays,
    decimal? DisplayDailyPrice,
    bool HasActivePrice,
    Guid? PricingRecordId,
    string? SourcePackageCode,
    int? SourceDurationDays);
public sealed record PlanPriceResponse(
    int DurationDays,
    int MealsPerDay,
    int SnacksPerDay,
    decimal Amount,
    string CurrencyCode,
    string? Name = null,
    string? Description = null);
public sealed record MealPlanResponse(Guid Id, string Code, string Name, string? Description, string PlanType, int DurationDays, bool IsCustomizable, IReadOnlyList<PlanPriceResponse> Prices, IReadOnlyList<MealTypeResponse> SupportedMealTypes);
public sealed record MealPlanPurchasePlanResponse(
    Guid Id,
    string Code,
    string Name,
    string? ShortDescription,
    string? ImageUrl,
    decimal? EstimatedCaloriesPerDay);
public sealed record MealPlanPurchasePackageResponse(
    Guid PriceId,
    string PackageId,
    string PackageCode,
    string PackageName,
    int ServiceDays,
    int DisplayOrder,
    string CurrencyCode,
    decimal Amount,
    decimal PricePerServiceDay,
    string? Name = null,
    string? Description = null);
public sealed record MealPlanMealConfigurationResponse(
    int MealsPerDay,
    int SnacksPerDay,
    string DisplayName,
    string IncludedText,
    IReadOnlyList<MealPlanPurchasePackageResponse> Packages);
public sealed record MealPlanPurchaseOptionsResponse(
    MealPlanPurchasePlanResponse Plan,
    IReadOnlyList<MealPlanMealConfigurationResponse> MealConfigurations,
    bool HasRecordedAllergens);
public sealed record ValidateMealPlanSelectionRequest(
    Guid MealPlanTemplateId,
    Guid MealPlanPriceId);
public sealed record MealPlanSelectionValidationResponse(
    bool IsValid,
    Guid MealPlanTemplateId,
    Guid MealPlanPriceId,
    int MealsPerDay,
    int SnacksPerDay,
    int ServiceDays,
    string CurrencyCode,
    decimal Amount,
    decimal PricePerServiceDay);
public sealed record CalendarDayResponse(Guid TemplateDayId, DateOnly Date, MenuWeekday MenuWeekday, string DayShortName, string DayName, bool IsAvailable);
public sealed record MealTypeResponse(Guid? Id, string Code, string Name, int DisplayOrder);
public sealed record MealCardResponse(Guid SlotOptionId, Guid SlotId, Guid MealItemId, MealTypeResponse MealType, string Name, string? ShortDescription, string? ThumbnailUrl, decimal? CaloriesKcal, decimal? ProteinGrams, decimal? CarbohydratesGrams, decimal? FatGrams, decimal AdditionalPrice, string CurrencyCode, bool IsDefault, bool IsAvailable, IReadOnlyList<string> AllergenCodes);
public sealed record MealSearchResponse(Guid MealItemId, string Sku, string Name, string? ShortDescription, string? ThumbnailUrl, decimal? CaloriesKcal, decimal? ProteinGrams, decimal? CarbohydratesGrams, decimal? FatGrams, decimal? Price, string? CurrencyCode, bool IsAvailable);

public sealed record GuestHomeQuery(
    string Language = "en",
    DateOnly? Date = null,
    string? PlanCode = null);
public sealed record GuestMenuQuery(
    DateOnly Date,
    string Language = "en");
public sealed record GuestAllergensQuery(string Language = "en");
public sealed record GuestAllergenLookupResponse(Guid Id, string Code, string Name);
public sealed record GuestPlanSummaryResponse(
    Guid Id,
    string Code,
    string Name,
    string Description,
    string? ImageUrl,
    string? IconUrl,
    int DisplayOrder,
    bool IsSelected,
    IReadOnlyList<GuestSlotResponse> Slots);
public sealed record GuestCalendarDayResponse(DateOnly Date, int DayNumber, string DayName, string ShortDayName, bool IsToday, bool IsSelected, bool IsAvailable);
public sealed record GuestSlotMealTimeResponse(Guid Id, string Code, string Name, int DisplayOrder);
public sealed record GuestSlotResponse(
    Guid Id,
    GuestSlotMealTimeResponse MealTime,
    int DisplayOrder,
    int MinimumSelection,
    int MaximumSelection,
    bool IsRequired);
public sealed record GuestNutritionResponse(decimal? Calories, decimal? Protein, decimal? Carbs, decimal? Fat, decimal? Fiber);
public sealed record GuestCodeNameResponse(string Code, string Name);
public sealed record GuestMealSlotResponse(
    Guid Id,
    GuestSlotMealTimeResponse MealTime,
    int DisplayOrder,
    int MinimumSelection,
    int MaximumSelection,
    bool IsRequired,
    IReadOnlyList<GuestMealResponse> Meals);
public sealed record GuestMealResponse(
    Guid Id,
    string Code,
    string Name,
    string Description,
    string? ImageUrl,
    string? ThumbnailUrl,
    GuestNutritionResponse Nutrition,
    IReadOnlyList<GuestCodeNameResponse> Tags,
    IReadOnlyList<GuestCodeNameResponse> Allergens,
    bool IsAvailable,
    int DisplayOrder);
public sealed record GuestMenuResponse(
    Guid PlanId,
    string PlanCode,
    DateOnly Date,
    IReadOnlyList<GuestMealSlotResponse> Slots);
public sealed record GuestHomeResponse(
    IReadOnlyList<GuestPlanSummaryResponse> MealPlans,
    IReadOnlyList<GuestCalendarDayResponse> WeeklyCalendar);

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

public sealed class UpsertCustomerProfileRequest
{
    private List<CustomerPreferenceRequest> preferences = [];
    private List<CustomerAllergenRequest> allergens = [];

    public string? GenderCode { get; init; }
    public DateOnly? DateOfBirth { get; init; }
    public decimal? HeightCm { get; init; }
    public decimal? WeightKg { get; init; }
    public string? GoalCode { get; init; }
    public string? DailyRoutineCode { get; init; }
    public string? ActivityLevelCode { get; init; }
    public string PreferredLanguage { get; init; } = "en";
    public string OnboardingStatus { get; init; } = "IN_PROGRESS";
    public List<CustomerPreferenceRequest> Preferences { get => preferences; init => preferences = value ?? []; }
    public List<CustomerAllergenRequest> Allergens { get => allergens; init => allergens = value ?? []; }
}

public sealed record CustomerPreferenceRequest(
    string PreferenceCode,
    string? PreferenceType,
    int PreferencePriority);
public sealed record CustomerAllergenRequest(
    Guid AllergenId,
    string? SeverityCode,
    bool MedicallyConfirmed,
    string? Notes);
public sealed record UpdateCustomerPreferredNameRequest(string PreferredName);
public sealed record UpdateCustomerPersonalInfoRequest(
    string FullName,
    DateOnly? DateOfBirth,
    string Gender);
public sealed record CustomerProfileAddressResponse(
    Guid Id,
    string? Label,
    bool IsDefault,
    string? Unit,
    string? Building,
    string? Street,
    string? Zone,
    string Area,
    string? LocationDescription,
    decimal? Latitude,
    decimal? Longitude);
public sealed record CustomerPersonalInfoResponse(
    Guid Id,
    string FullName,
    string? MobileNumber,
    DateOnly? DateOfBirth,
    string Gender,
    IReadOnlyList<CustomerProfileAddressResponse> Addresses);
public sealed record CustomerNutritionTargetResponse(
    int? DailyCaloriesKcal,
    decimal? DailyProteinG,
    decimal? DailyCarbohydratesG,
    decimal? DailyFatG,
    decimal? DailyFiberG,
    int? DailyWaterMl,
    string? CalculationMethod,
    string? CalculationVersion,
    DateTimeOffset CalculatedAt);
public sealed record CustomerPreferenceResponse(
    Guid Id,
    string PreferenceCode,
    string? PreferenceType,
    int PreferencePriority);
public sealed record CustomerAllergenResponse(
    Guid Id,
    Guid AllergenId,
    string AllergenCode,
    string? AllergenName,
    string? SeverityCode,
    bool MedicallyConfirmed,
    string? Notes);
public sealed record CustomerProfileResponse(
    Guid Id,
    Guid UserId,
    string? PreferredName,
    string? GenderCode,
    DateOnly? DateOfBirth,
    int? Age,
    decimal? HeightCm,
    decimal? WeightKg,
    decimal? Bmi,
    string? BmiCategoryCode,
    string? GoalCode,
    string? DailyRoutineCode,
    string? ActivityLevelCode,
    string PreferredLanguage,
    string OnboardingStatus,
    DateTimeOffset? OnboardingCompletedAt,
    bool IsActive,
    CustomerNutritionTargetResponse? NutritionTarget,
    IReadOnlyList<CustomerPreferenceResponse> Preferences,
    IReadOnlyList<CustomerAllergenResponse> Allergens,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long RowVersion);

public sealed record UpsertCustomerAddressRequest(
    string? AddressName,
    string AddressType,
    string? BuildingNo,
    string? StreetNo,
    string? UnitNumber,
    string? ZoneNo,
    string Area,
    string? Directions,
    decimal? Latitude,
    decimal? Longitude,
    string? FormattedAddress,
    bool IsDefault = false);

public sealed record CustomerAddressResponse(
    Guid Id,
    Guid CustomerProfileId,
    string? AddressName,
    string AddressType,
    string? BuildingNo,
    string? StreetNo,
    string? UnitNumber,
    string? ZoneNo,
    string Area,
    string? Directions,
    decimal? Latitude,
    decimal? Longitude,
    string? FormattedAddress,
    bool IsDefault);

public sealed record CustomerAddressListResponse(IReadOnlyList<CustomerAddressResponse> Items);
public sealed record DeliveryTimeSlotResponse(
    Guid Id,
    string Code,
    string Name,
    string NameAr,
    TimeOnly StartTime,
    TimeOnly EndTime);
public sealed record DeliveryTimeSlotListResponse(IReadOnlyList<DeliveryTimeSlotResponse> Items);

public sealed record PlaceOrderMealRequest(Guid MealTypeId, int Quantity);
public sealed record PlaceOrderRequest(
    Guid CustomerProfileId,
    Guid MealPlanTemplateId,
    Guid MealPlanPriceId,
    Guid CustomerAddressId,
    Guid DeliveryTimeSlotId,
    DateOnly StartDate,
    IReadOnlyList<int> DeliveryDays,
    IReadOnlyList<PlaceOrderMealRequest> Meals,
    string? CouponCode = null);

public sealed record OrderPlanResponse(
    Guid MealPlanTemplateId, Guid MealPlanPriceId, string Name, string DurationName);
public sealed record OrderMealResponse(Guid MealTypeId, string Name, int Quantity);
public sealed record OrderTimeSlotResponse(Guid Id, string Name, TimeOnly StartTime, TimeOnly EndTime);
public sealed record OrderAddressResponse(
    Guid AddressId,
    string? AddressName,
    string AddressType,
    string? BuildingNo,
    string? StreetNo,
    string? UnitNumber,
    string? ZoneNo,
    string Area,
    string? Directions,
    decimal? Latitude,
    decimal? Longitude,
    string? FormattedAddress);
public sealed record OrderDeliveryResponse(
    int DaysPerWeek,
    IReadOnlyList<int> Days,
    DateOnly StartDate,
    DateOnly EndDate,
    OrderTimeSlotResponse TimeSlot,
    OrderAddressResponse Address);
public sealed record OrderPricingResponse(
    decimal Subtotal,
    decimal DiscountAmount,
    decimal DeliveryCharge,
    decimal TotalAmount,
    string CurrencyCode);
public sealed record PlaceOrderResponse(
    Guid Id,
    string OrderNumber,
    string Status,
    string PaymentStatus,
    OrderPlanResponse Plan,
    IReadOnlyList<OrderMealResponse> Meals,
    OrderDeliveryResponse Delivery,
    OrderPricingResponse Pricing,
    DateTimeOffset PlacedAt);
public sealed record CustomerOrderSummaryResponse(
    Guid Id,
    string OrderNumber,
    string PlanName,
    string PlanDurationName,
    DateOnly StartDate,
    DateOnly EndDate,
    string Status,
    string PaymentStatus,
    decimal TotalAmount,
    string CurrencyCode,
    DateTimeOffset PlacedAt);
public sealed record CustomerOrdersResponse(
    IReadOnlyList<CustomerOrderSummaryResponse> Items,
    int PageNumber,
    int PageSize,
    int TotalCount);

public sealed record ChangeMealStatusRequest(string Status);
public sealed record AdminMealSummaryResponse(
    Guid Id,
    string Sku,
    string Status,
    bool IsAvailable,
    string Name,
    string? NameAr,
    DateTimeOffset? AvailableFrom,
    DateTimeOffset? AvailableUntil,
    CategoryResponse Category,
    NutritionResponse? Nutrition,
    MoneyResponse? Price,
    DateTimeOffset UpdatedAt,
    int VersionNumber);
public sealed record AdminMealResponse(Guid Id, string Status, UpsertMealRequest Meal, IReadOnlyList<AdminMediaResponse> Media, Guid VersionGroupId, int VersionNumber, bool IsLatest);
public sealed record VersionedUpdateResponse(Guid Id, bool CreatedDraft);
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
public sealed record UpsertMealRequest(string Sku, Guid CategoryId, int? PreparationTimeMinutes, bool IsVegetarian, bool IsVegan, bool IsGlutenFree, bool IsDairyFree, bool IsAvailable, DateTimeOffset? AvailableFrom, DateTimeOffset? AvailableUntil, IReadOnlyList<AdminTranslationRequest> Translations, AdminNutritionRequest? Nutrition, IReadOnlyList<AdminIngredientLinkRequest>? Ingredients = null, IReadOnlyList<AdminAllergenLinkRequest>? Allergens = null, IReadOnlyList<AdminPriceRequest>? Prices = null, string? Status = null, bool? IsSpicy = null, short? SpiceLevel = null, bool? IsNutFree = null);
public sealed record SaveMediaRequest(string ObjectKey, string? PublicUrl, string ContentType, string MediaType, bool IsPrimary, int DisplayOrder, string? AltTextEn);
public sealed record SaveThumbnailRequest(string ObjectKey, string? PublicUrl);
public sealed record AdminThumbnailUpdateResponse(AdminMediaResponse Media, string? PreviousObjectKey);
public sealed record AdminMediaResponse(Guid Id, Guid MealItemId, string MediaType, string ObjectKey, string? PublicUrl, string ContentType, bool IsPrimary, int DisplayOrder, string Status, string? AltTextEn, string? ThumbnailObjectKey, string? ThumbnailUrl);
public sealed record AdminMealPlanSummaryResponse(
    Guid Id,
    string Code,
    string Name,
    string? ShortDescription,
    string PlanType,
    int DurationDays,
    bool IsCustomizable,
    bool IsPublished,
    bool IsActive,
    DateOnly? ValidFrom,
    DateOnly? ValidUntil,
    DateTimeOffset UpdatedAt,
    Guid VersionGroupId,
    int VersionNumber);
public sealed record AdminPlanTranslationResponse(string LanguageCode, string Name, string? ShortDescription, string? FullDescription);
public sealed record AdminPlanOptionResponse(Guid Id, Guid MealItemId, string MealName, bool IsDefault);
public sealed record AdminPlanSlotResponse(Guid Id, Guid MealTypeId, string MealTypeName, int DisplayOrder, int MinimumSelection, int MaximumSelection, bool IsRequired, IReadOnlyList<AdminPlanOptionResponse> Options);
public sealed record AdminPlanDayResponse(Guid Id, Guid TemplateId, MenuWeekday MenuWeekday, int DisplayOrder, bool IsActive, int SlotCount, IReadOnlyList<AdminPlanSlotResponse> Slots);
public sealed record AdminMealPlanDetailResponse(Guid Id, string Code, string PlanType, int DurationDays, bool IsCustomizable, bool IsPublished, bool IsActive, DateOnly? ValidFrom, DateOnly? ValidUntil, string? ImageUrl, string? ImageType, IReadOnlyList<AdminPlanTranslationResponse> Translations, IReadOnlyList<AdminPlanDayResponse> Days, Guid VersionGroupId, int VersionNumber, bool IsLatest);
public sealed record AdminPlanImageResponse(Guid PlanId, string ImageType, string PublicUrl, string ContentType);
public sealed record AdminMealPlanPriceResponse(
    Guid Id,
    Guid MealPlanTemplateId,
    string MealPlanCode,
    string MealPlanName,
    int DurationDays,
    int MealsPerDay,
    int SnacksPerDay,
    string CurrencyCode,
    decimal Amount,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveUntil,
    bool IsActive,
    string Status,
    bool CanDelete,
    string? MealPlanPricePackageId = null,
    string? PackageCode = null,
    string? PackageNameEn = null,
    string? PackageNameAr = null,
    IReadOnlyList<AdminMealPlanPriceTranslationResponse>? Translations = null);
public sealed record AdminMealPlanPriceTranslationResponse(
    string LanguageCode,
    string Name,
    string? Description);
public sealed record UpsertMealPlanPriceTranslationRequest(
    string LanguageCode,
    string Name,
    string? Description = null);
public sealed record AdminMealPlanPriceSummaryResponse(int Active, int Scheduled, int Expired, int Inactive);
public sealed record UpsertMealPlanPriceRequest(
    Guid MealPlanTemplateId,
    int? DurationDays,
    int MealsPerDay,
    int SnacksPerDay,
    string CurrencyCode,
    decimal Amount,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveUntil,
    bool IsActive,
    string? MealPlanPricePackageId = null,
    IReadOnlyList<UpsertMealPlanPriceTranslationRequest>? Translations = null);
public sealed record SetMealPlanPriceStatusRequest(bool IsActive);
public sealed record MealPlanPricePackageResponse(
    string Id,
    string Code,
    string NameEn,
    string NameAr,
    int DurationDays,
    int DisplayOrder,
    bool IsActive,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt);
public sealed record MealPlanPricePackageLookupResponse(
    string Id,
    string Code,
    string Name,
    string NameEn,
    string NameAr,
    int DurationDays,
    int DisplayOrder);
public sealed record UpsertMealPlanPricePackageRequest(
    string Code,
    string NameEn,
    string NameAr,
    int DurationDays,
    int DisplayOrder,
    bool IsActive);
public sealed record SetMealPlanPricePackageStatusRequest(bool IsActive);
public sealed record UpsertPlanOptionRequest(Guid MealItemId, decimal AdditionalPrice, bool IsDefault, bool IsAvailable, int DisplayOrder);
public sealed record UpsertPlanSlotRequest(Guid MealTypeId, int DisplayOrder, int MinimumSelection, int MaximumSelection, bool IsRequired, TimeOnly? SelectionCutoffTime, bool AllowsPaidUpgrade, IReadOnlyList<UpsertPlanOptionRequest> Options);
public sealed record UpsertPlanDayRequest(MenuWeekday? MenuWeekday, int DisplayOrder, bool IsActive, IReadOnlyList<UpsertPlanSlotRequest> Slots);
public sealed record CreatePlanRequest(string Code, string PlanType, int DurationDays, bool IsCustomizable, DateOnly? ValidFrom, DateOnly? ValidUntil, IReadOnlyList<AdminTranslationRequest> Translations, IReadOnlyList<UpsertPlanDayRequest>? Days = null, bool Publish = false);
public sealed record UpsertMealPlanTemplateDayRequest(MenuWeekday? MenuWeekday, int DisplayOrder, bool IsActive = true);
public sealed record MealPlanTemplateDayResponse(Guid Id, Guid TemplateId, MenuWeekday MenuWeekday, int DisplayOrder, bool IsActive, int SlotCount);
public sealed record TemplateMenuOptionResponse(Guid Id, Guid MealItemId, string MealName, decimal AdditionalPrice, bool IsDefault, bool IsAvailable, int DisplayOrder);
public sealed record TemplateMenuSlotResponse(Guid Id, Guid MealTypeId, string MealTypeCode, string MealTypeName, int DisplayOrder, int MinimumSelection, int MaximumSelection, bool IsRequired, TimeOnly? SelectionCutoffTime, bool AllowsPaidUpgrade, bool IsActive, IReadOnlyList<TemplateMenuOptionResponse> Options);
public sealed record MealPlanTemplateDayDetailResponse(Guid Id, Guid TemplateId, MenuWeekday MenuWeekday, int DisplayOrder, bool IsActive, int SlotCount, IReadOnlyList<TemplateMenuSlotResponse> Slots);
public sealed record TemplateDayErrorResponse(string Code, string Message);
public sealed record CreatePlanSlotRequest(Guid MealTypeId, int DisplayOrder, int MinimumSelection, int MaximumSelection, bool IsRequired, TimeOnly? SelectionCutoffTime, bool AllowsPaidUpgrade);
public sealed record CreateSlotOptionRequest(Guid MealItemId, decimal AdditionalPrice, bool IsDefault, bool IsAvailable, int DisplayOrder);
public sealed record RegisterRequest(string Email, string Password, string? FirstName = null, string? LastName = null);
public sealed record LoginRequest(string Email, string Password);
public sealed record PhoneOtpLoginRequest(string PhoneNumber, string Otp, string? FirstName = null, string? LastName = null);
public sealed record RefreshRequest(string? RefreshToken = null);
public sealed record LogoutRequest(string? RefreshToken = null);
public sealed record AuthUserResponse(Guid Id, string Email, string Name, IReadOnlyList<string> Roles, string? PhoneNumber = null);
public sealed record AuthSessionResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    AuthUserResponse User);

// User Management DTOs
public sealed record UserProfileResponse(
    Guid Id,
    Guid UserId,
    string FirstName,
    string LastName,
    string FullName,
    string Email,
    string? Mobile,
    string Status,
    bool IsActive,
    bool IsCustomer,
    Guid? CustomerId,
    string? CreatedBy,
    DateTimeOffset CreatedAt,
    string? ModifiedBy,
    DateTimeOffset ModifiedAt);

public sealed record CreateUserProfileRequest(
    string Email,
    string FirstName,
    string LastName,
    string? Mobile,
    string Password,
    string Status = "ACTIVE",
    bool IsActive = true);

public sealed record UpdateUserProfileRequest(
    string FirstName,
    string LastName,
    string? Mobile,
    string Status,
    bool IsActive);

public sealed record CustomerResponse(
    Guid Id,
    string CustomerName,
    int? Age,
    string? Mobile,
    string? Email,
    string Status,
    bool IsActive,
    decimal? Weight,
    decimal? Height,
    decimal? BMI,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateCustomerRequest(
    string CustomerName,
    int? Age,
    string? Mobile,
    string? Email,
    decimal? Weight,
    decimal? Height,
    string Status = "ACTIVE",
    bool IsActive = true);

public sealed record UpdateCustomerRequest(
    string CustomerName,
    int? Age,
    string? Mobile,
    string? Email,
    decimal? Weight,
    decimal? Height,
    string Status,
    bool IsActive);

public sealed record ApplicationRoleResponse(
    Guid Id,
    string RoleName,
    string? Description,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateApplicationRoleRequest(
    string RoleName,
    string? Description,
    bool IsActive = true);

public sealed record UpdateApplicationRoleRequest(
    string RoleName,
    string? Description,
    bool IsActive);

public sealed record MenuResponse(
    Guid Id,
    string MainMenuCode,
    string MainMenuName,
    string SubMenuCode,
    string SubMenuName,
    string? RouteUrl,
    string? Icon,
    int DisplayOrder,
    bool IsActive,
    string? CreatedBy,
    DateTimeOffset CreatedAt,
    string? UpdatedBy,
    DateTimeOffset UpdatedAt);

public sealed record CreateMenuRequest(
    string MainMenuCode,
    string MainMenuName,
    string SubMenuCode,
    string SubMenuName,
    string? RouteUrl,
    string? Icon,
    int DisplayOrder,
    bool IsActive = true);

public sealed record UpdateMenuRequest(
    string MainMenuCode,
    string MainMenuName,
    string SubMenuCode,
    string SubMenuName,
    string? RouteUrl,
    string? Icon,
    int DisplayOrder,
    bool IsActive);

public sealed record RoleMenuMappingResponse(
    Guid Id,
    Guid RoleId,
    string RoleName,
    Guid MenuId,
    string MenuName,
    DateTimeOffset CreatedAt);

public sealed record CreateRoleMenuMappingRequest(
    Guid RoleId,
    Guid MenuId);

public sealed record RoleMenusResponse(
    Guid RoleId,
    string RoleName,
    IReadOnlyList<MenuResponse> Menus);

public sealed record ScreenPermissionResponse(
    Guid ScreenId,
    string GroupCode,
    string GroupName,
    string ScreenCode,
    string ScreenName,
    string? RouteUrl,
    string? Icon,
    int DisplayOrder,
    bool IsActive,
    bool CanRead,
    bool CanWrite);

public sealed record ScreenPermissionRequest(Guid ScreenId, bool CanRead, bool CanWrite);

public sealed record AccessRoleResponse(
    Guid Id,
    string RoleName,
    string? Description,
    bool IsActive,
    IReadOnlyList<ScreenPermissionResponse> Screens);

public sealed record SaveAccessRoleRequest(
    string RoleName,
    string? Description,
    bool IsActive,
    IReadOnlyList<ScreenPermissionRequest> Screens);

public sealed record AccessUserResponse(
    Guid ProfileId,
    Guid UserId,
    string Email,
    string FirstName,
    string LastName,
    string? Mobile,
    string Status,
    bool IsActive,
    IReadOnlyList<Guid> RoleIds,
    IReadOnlyList<string> RoleNames);

public sealed record CreateAccessUserRequest(
    string Email,
    string FirstName,
    string LastName,
    string? Mobile,
    string Password,
    bool IsActive,
    IReadOnlyList<Guid> RoleIds);

public sealed record UpdateAccessUserRequest(
    string Email,
    string FirstName,
    string LastName,
    string? Mobile,
    string? Password,
    bool IsActive,
    IReadOnlyList<Guid> RoleIds);

// Password management DTOs

public sealed record RequestPasswordResetRequest(
    string Email);

public sealed record RequestPasswordResetResponse(
    string Message,
    bool Success);

public sealed record SetPasswordRequest(
    string ResetToken,
    string NewPassword);

public sealed record SetPasswordResponse(
    string Message,
    bool Success);
