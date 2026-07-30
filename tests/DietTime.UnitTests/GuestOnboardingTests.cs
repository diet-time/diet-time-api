using DietTime.Application;
using DietTime.Contracts;
using DietTime.Infrastructure;
using System.Text.Json;

namespace DietTime.UnitTests;

public sealed class GuestOnboardingTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
    private readonly GuestProfileOptions options = new();

    [Fact]
    public void Token_generation_is_secure_unique_and_has_the_configured_expiry()
    {
        var generator = new GuestTokenGenerator(options, new FixedTimeProvider(Now));

        var first = generator.Generate();
        var second = generator.Generate();

        Assert.NotEqual(first.RawToken, second.RawToken);
        Assert.Equal(Now.AddDays(30), first.ExpiresAt);
        Assert.Equal(43, first.RawToken.Length);
        Assert.DoesNotContain("=", first.RawToken);
    }

    [Fact]
    public void Token_hashing_is_stable_and_verification_rejects_other_tokens()
    {
        var generator = new GuestTokenGenerator(options, new FixedTimeProvider(Now));
        var hasher = new GuestTokenHasher(options);
        var token = generator.Generate().RawToken;
        var otherToken = generator.Generate().RawToken;

        var hash = hasher.Hash(token);

        Assert.Equal(64, hash.Length);
        Assert.DoesNotContain(token, hash);
        Assert.True(hasher.Verify(token, hash));
        Assert.False(hasher.Verify(otherToken, hash));
        Assert.False(hasher.IsValidFormat("not-a-valid-token"));
    }

    [Fact]
    public void Guest_profile_allows_partial_onboarding()
    {
        var validator = new UpsertGuestProfileRequestValidator(new FixedTimeProvider(Now));

        var result = validator.Validate(new UpsertGuestProfileRequest
        {
            GenderCode = "MALE",
            DateOfBirth = new DateOnly(1990, 6, 15),
            PreferredLanguage = "en",
            OnboardingStatus = "IN_PROGRESS"
        });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Guest_profile_request_tracks_omitted_and_explicit_properties()
    {
        var request = JsonSerializer.Deserialize<UpsertGuestProfileRequest>(
            """
            {
              "heightCm": 175,
              "preferences": []
            }
            """,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.True(request.HeightCmSupplied);
        Assert.Equal(175m, request.HeightCm);
        Assert.False(request.WeightKgSupplied);
        Assert.True(request.PreferencesSupplied);
        Assert.Empty(request.Preferences);
        Assert.False(request.AllergensSupplied);
    }

    [Fact]
    public void Client_completed_status_is_accepted_for_server_side_correction()
    {
        var validator = new UpsertGuestProfileRequestValidator(new FixedTimeProvider(Now));

        var result = validator.Validate(new UpsertGuestProfileRequest
        {
            PreferredLanguage = "en",
            OnboardingStatus = "PROFILE_COMPLETED"
        });

        Assert.True(result.IsValid);
    }

    [Theory]
    [MemberData(nameof(ProgressCases))]
    public void Progress_resolver_returns_first_incomplete_step_and_percentage(
        GuestOnboardingProgressInput input,
        string expectedStep,
        int expectedPercentage)
    {
        var result = new GuestOnboardingProgressResolver().Resolve(input);

        Assert.Equal(expectedStep, result.NextStepCode);
        Assert.Equal(expectedPercentage, result.CompletionPercentage);
        Assert.Equal(expectedStep != "PROFILE_COMPLETED", result.ShouldShowOnboarding);
    }

    public static TheoryData<GuestOnboardingProgressInput, string, int> ProgressCases =>
        new()
        {
            { Progress(), "BASIC_DETAILS", 0 },
            { Progress(gender: "MALE", dateOfBirth: new(1990, 6, 15)), "BODY_MEASUREMENTS", 14 },
            { Progress(gender: "MALE", dateOfBirth: new(1990, 6, 15), height: 175, weight: 82), "GOAL", 29 },
            { Progress(gender: "MALE", dateOfBirth: new(1990, 6, 15), height: 175, weight: 82, goal: "LOSE_WEIGHT"), "DAILY_ROUTINE", 43 },
            { Progress(gender: "MALE", dateOfBirth: new(1990, 6, 15), height: 175, weight: 82, goal: "LOSE_WEIGHT", routine: "OFFICE_WORK"), "ACTIVITY_LEVEL", 57 },
            { Progress(gender: "MALE", dateOfBirth: new(1990, 6, 15), height: 175, weight: 82, goal: "LOSE_WEIGHT", routine: "OFFICE_WORK", activity: "LIGHT_ACTIVITY"), "ALLERGENS", 71 },
            { Progress(gender: "MALE", dateOfBirth: new(1990, 6, 15), height: 175, weight: 82, goal: "LOSE_WEIGHT", routine: "OFFICE_WORK", activity: "LIGHT_ACTIVITY", allergensConfirmed: true), "PREFERENCES", 86 },
            { Progress(gender: "MALE", dateOfBirth: new(1990, 6, 15), height: 175, weight: 82, goal: "LOSE_WEIGHT", routine: "OFFICE_WORK", activity: "LIGHT_ACTIVITY", allergensConfirmed: true, preferencesConfirmed: true), "PROFILE_COMPLETED", 100 }
        };

    [Theory]
    [InlineData("COMPLETED")]
    [InlineData("ACCOUNT_LINKED")]
    [InlineData("SUBSCRIPTION_COMPLETED")]
    public void Unsupported_guest_workflow_statuses_are_rejected(string status)
    {
        var validator = new UpsertGuestProfileRequestValidator(new FixedTimeProvider(Now));

        var result = validator.Validate(new UpsertGuestProfileRequest
        {
            PreferredLanguage = "en",
            OnboardingStatus = status
        });

        Assert.False(result.IsValid);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static GuestOnboardingProgressInput Progress(
        string? gender = null,
        DateOnly? dateOfBirth = null,
        decimal? height = null,
        decimal? weight = null,
        string? goal = null,
        string? routine = null,
        string? activity = null,
        bool allergensConfirmed = false,
        bool preferencesConfirmed = false) =>
        new(
            gender,
            dateOfBirth,
            height,
            weight,
            goal,
            routine,
            activity,
            allergensConfirmed,
            preferencesConfirmed);
}
