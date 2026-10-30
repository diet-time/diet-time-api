using DietTime.Domain;

namespace DietTime.UnitTests;

public sealed class QuestionnaireCompletionTests
{
    private static readonly DateOnly Today = new(2026, 10, 30);

    [Fact]
    public void A_status_flag_alone_does_not_complete_a_questionnaire()
    {
        var profile = new CustomerProfile { OnboardingStatus = "COMPLETED" };
        Assert.False(CustomerQuestionnaireRules.IsComplete(profile, Today));
        Assert.Equal("BASIC_DETAILS", CustomerQuestionnaireRules.NextStep(profile, Today));
    }

    [Fact]
    public void Legacy_completed_profiles_with_valid_answers_remain_complete()
    {
        var profile = Complete();
        Assert.True(CustomerQuestionnaireRules.IsComplete(profile, Today));
        profile.GoalCode = null;
        Assert.False(CustomerQuestionnaireRules.IsComplete(profile, Today));
        Assert.Equal("GOAL", CustomerQuestionnaireRules.NextStep(profile, Today));
    }

    [Fact]
    public void Partial_answers_never_indicate_completion_even_when_all_fields_exist()
    {
        var profile = Complete();
        profile.OnboardingStatus = "IN_PROGRESS";
        profile.AllergensConfirmed = profile.PreferencesConfirmed = true;
        Assert.False(CustomerQuestionnaireRules.IsComplete(profile, Today));
    }

    [Fact]
    public void Invalid_measurements_or_dates_cannot_complete_a_profile()
    {
        var profile = Complete();
        profile.WeightKg = 0;
        Assert.False(CustomerQuestionnaireRules.IsComplete(profile, Today));
        profile.WeightKg = 70;
        profile.DateOfBirth = Today.AddDays(1);
        Assert.False(CustomerQuestionnaireRules.IsComplete(profile, Today));
    }

    private static CustomerProfile Complete() => new()
    {
        GenderCode = "FEMALE", DateOfBirth = new(1995, 1, 1), HeightCm = 165, WeightKg = 70,
        GoalCode = "LOSE_WEIGHT", DailyRoutineCode = "OFFICE_WORK", ActivityLevelCode = "MODERATE",
        OnboardingStatus = "COMPLETED"
    };
}
