namespace DietTime.Domain;

public static class CustomerQuestionnaireRules
{
    public static string NextStep(CustomerProfile profile, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(profile.GenderCode) || profile.DateOfBirth is null ||
            profile.DateOfBirth > today || profile.DateOfBirth < today.AddYears(-120)) return "BASIC_DETAILS";
        if (profile.HeightCm is null or < 50 or > 300 || profile.WeightKg is null or < 15 or > 500) return "BODY_MEASUREMENTS";
        if (string.IsNullOrWhiteSpace(profile.GoalCode)) return "GOAL";
        if (string.IsNullOrWhiteSpace(profile.DailyRoutineCode)) return "DAILY_ROUTINE";
        if (string.IsNullOrWhiteSpace(profile.ActivityLevelCode)) return "ACTIVITY_LEVEL";
        // Older completed profiles predate the explicit confirmation flags.
        if (string.Equals(profile.OnboardingStatus, "COMPLETED", StringComparison.OrdinalIgnoreCase)) return "PROFILE_COMPLETED";
        if (!profile.PreferencesConfirmed) return "PREFERENCES";
        return "ALLERGENS";
    }

    public static bool IsComplete(CustomerProfile profile, DateOnly today) =>
        NextStep(profile, today) == "PROFILE_COMPLETED";
}
