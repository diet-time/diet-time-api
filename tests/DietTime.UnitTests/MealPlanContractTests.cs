using System.Text.Json;
using DietTime.Contracts;
using DietTime.Domain;

namespace DietTime.UnitTests;

public sealed class MealPlanContractTests
{
    [Fact]
    public void Upsert_plan_request_deserializes_publish_intent()
    {
        const string json = """
            {
              "code": "PLN_CLASSIC",
              "planType": "STANDARD",
              "durationDays": 20,
              "isCustomizable": true,
              "validFrom": null,
              "validUntil": null,
              "translations": [{ "languageCode": "en", "name": "Classic" }],
              "days": [],
              "publish": true
            }
            """;

        var request = JsonSerializer.Deserialize<CreatePlanRequest>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(request);
        Assert.True(request.Publish);
    }

    [Fact]
    public void Plan_image_response_identifies_meal_plan_image_type()
    {
        var response = new AdminPlanImageResponse(
            Guid.NewGuid(),
            "MEALPLAN",
            "/api/v1/media/meal-plans/plan/image.jpg",
            "image/jpeg");

        Assert.Equal("MEALPLAN", response.ImageType);
    }

    [Fact]
    public void Meal_plan_image_type_is_stored_on_meal_media_not_plan_template()
    {
        Assert.Null(typeof(MealPlanTemplate).GetProperty("ImageType"));
        Assert.Null(typeof(MealMedia).GetProperty("MealItemId"));
        Assert.Null(typeof(MealMedia).GetProperty("MealPlanTemplateId"));
        Assert.NotNull(typeof(MealMedia).GetProperty("EntityId"));

        var media = new MealMedia
        {
            EntityId = Guid.NewGuid(),
            MediaType = "MEALPLAN"
        };

        Assert.NotEqual(Guid.Empty, media.EntityId);
        Assert.Equal("MEALPLAN", media.MediaType);
    }
}
