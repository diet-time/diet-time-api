using Amazon.S3;
using DietTime.Application;
using DietTime.Domain;
using DietTime.Infrastructure;
using Microsoft.Extensions.Options;

namespace DietTime.UnitTests;

public sealed class DomainRuleTests
{
    [Fact] public void Localization_uses_requested_then_english_then_first() { var values = new[] { new LocalizedValue("fr", "Premier"), new LocalizedValue("en", "English"), new LocalizedValue("ar", "عربي") }; Assert.Equal("عربي", LocalizationFallback.Resolve(values, "ar")); Assert.Equal("English", LocalizationFallback.Resolve(values, "de")); Assert.Equal("Premier", LocalizationFallback.Resolve(values.Where(x => x.LanguageCode != "en"), "de")); }
    [Fact] public void Availability_requires_active_flag_and_valid_window() { var now = DateTimeOffset.UtcNow; Assert.True(MealAvailability.IsAvailable("ACTIVE", true, now.AddHours(-1), now.AddHours(1), now)); Assert.False(MealAvailability.IsAvailable("INACTIVE", true, null, null, now)); Assert.False(MealAvailability.IsAvailable("ACTIVE", true, now.AddMinutes(1), null, now)); }
    [Theory] [InlineData("DRAFT")] [InlineData("ACTIVE")] [InlineData("INACTIVE")] [InlineData("ARCHIVED")] public void Meal_statuses_accept_supported_lifecycle_values(string status) { Assert.True(MealStatuses.IsValid(status)); }
    [Fact] public void Slot_count_enforces_minimum_and_maximum() { Assert.True(SelectionRules.IsCountValid(1, 1, 2)); Assert.False(SelectionRules.IsCountValid(0, 1, 2)); Assert.False(SelectionRules.IsCountValid(3, 1, 2)); }
    [Fact] public void Duplicate_selection_ids_are_detectable() { var values = new[] { Guid.NewGuid(), Guid.NewGuid() }; var duplicate = values.Append(values[0]).GroupBy(x => x).Any(x => x.Count() > 1); Assert.True(duplicate); }
    [Fact] public void Price_resolution_honors_paid_upgrade_setting() { Assert.Equal(7m, SelectionRules.ResolveAdditionalPrice(7m, true)); Assert.Equal(0m, SelectionRules.ResolveAdditionalPrice(7m, false)); }
    [Fact] public void Image_url_resolution_escapes_each_object_key_segment() { var options = Options.Create(new StorageOptions { PublicBaseUrl = "https://cdn.example.test/", BucketName = "meals" }); using var s3 = new AmazonS3Client("test", "test", new AmazonS3Config { ServiceURL = "http://localhost:9000", ForcePathStyle = true }); var service = new StorageUrlService(options, s3); Assert.Equal("https://cdn.example.test/meals/item%201.webp", service.GetPublicUrl("meals/item 1.webp")); }
    [Fact] public void Meal_media_defaults_to_meal_item_type() { Assert.Equal("MEALITEM", new MealMedia().MediaType); }
    [Fact] public void Meal_plan_media_uses_distinct_type() { Assert.Equal("MEALPLAN", MealMediaTypes.MealPlan); }
}
