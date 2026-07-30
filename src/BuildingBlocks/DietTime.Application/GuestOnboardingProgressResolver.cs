namespace DietTime.Application;

public sealed class GuestOnboardingProgressResolver : IGuestOnboardingProgressResolver
{
    private const int TotalSteps = 7;

    public GuestOnboardingProgressResult Resolve(GuestOnboardingProgressInput input)
    {
        var completedSteps = 0;
        string? nextStep = null;

        CountStep(
            !string.IsNullOrWhiteSpace(input.GenderCode) && input.DateOfBirth.HasValue,
            "BASIC_DETAILS");
        CountStep(
            input.HeightCm.HasValue && input.WeightKg.HasValue,
            "BODY_MEASUREMENTS");
        CountStep(!string.IsNullOrWhiteSpace(input.GoalCode), "GOAL");
        CountStep(!string.IsNullOrWhiteSpace(input.DailyRoutineCode), "DAILY_ROUTINE");
        CountStep(!string.IsNullOrWhiteSpace(input.ActivityLevelCode), "ACTIVITY_LEVEL");
        CountStep(input.AllergensConfirmed, "ALLERGENS");
        CountStep(input.PreferencesConfirmed, "PREFERENCES");

        var nextStepCode = nextStep ?? "PROFILE_COMPLETED";
        var completionPercentage = (int)Math.Round(
            completedSteps * 100m / TotalSteps,
            MidpointRounding.AwayFromZero);
        return new(
            nextStepCode,
            completionPercentage,
            nextStepCode != "PROFILE_COMPLETED");

        void CountStep(bool completed, string stepCode)
        {
            if (completed)
                completedSteps++;
            else
                nextStep ??= stepCode;
        }
    }
}
