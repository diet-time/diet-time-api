using DietTime.Domain;
using DietTime.Application;
using DietTime.Contracts;

namespace DietTime.UnitTests;

public sealed class CustomerOnboardingSequenceTests
{
    [Fact]
    public void Required_steps_follow_the_existing_customer_questionnaire_order()
    {
        var today = new DateOnly(2026, 10, 31);
        var profile = new CustomerProfile { OnboardingStatus = "IN_PROGRESS" };
        Assert.Equal("GOAL", CustomerQuestionnaireRules.NextStep(profile, today));
        profile.GoalCode = "LOSE_WEIGHT";
        Assert.Equal("BASIC_DETAILS", CustomerQuestionnaireRules.NextStep(profile, today));
        profile.GenderCode = "FEMALE"; profile.DateOfBirth = new(1995, 3, 17);
        Assert.Equal("BODY_MEASUREMENTS", CustomerQuestionnaireRules.NextStep(profile, today));
        profile.HeightCm = 165; profile.WeightKg = 70;
        Assert.Equal("DAILY_ROUTINE", CustomerQuestionnaireRules.NextStep(profile, today));
        profile.DailyRoutineCode = "OFFICE_WORK";
        Assert.Equal("ACTIVITY_LEVEL", CustomerQuestionnaireRules.NextStep(profile, today));
        profile.ActivityLevelCode = "MOSTLY_SITTING";
        Assert.Equal("PREFERENCES", CustomerQuestionnaireRules.NextStep(profile, today));
        profile.PreferencesConfirmed = true;
        Assert.Equal("ALLERGENS", CustomerQuestionnaireRules.NextStep(profile, today));
        profile.AllergensConfirmed = true;
        Assert.False(CustomerQuestionnaireRules.IsComplete(profile, today));
        profile.OnboardingStatus = "COMPLETED";
        Assert.Equal("PROFILE_COMPLETED", CustomerQuestionnaireRules.NextStep(profile, today));
    }

    [Theory]
    [InlineData("+974 7445 2435", false)]
    [InlineData("   ", false)]
    [InlineData(" x ", false)]
    [InlineData("Noor", true)]
    [InlineData("نور", true)]
    public void Required_names_are_validated_consistently(string name, bool valid)
        => Assert.Equal(valid, new UpdateCustomerPreferredNameRequestValidator().Validate(new UpdateCustomerPreferredNameRequest(name)).IsValid);
}