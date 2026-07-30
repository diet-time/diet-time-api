using DietTime.Application;
using DietTime.Contracts;
using DietTime.Infrastructure;

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
    public void Completed_guest_profile_requires_all_mandatory_answers()
    {
        var validator = new UpsertGuestProfileRequestValidator(new FixedTimeProvider(Now));

        var result = validator.Validate(new UpsertGuestProfileRequest
        {
            PreferredLanguage = "en",
            OnboardingStatus = "PROFILE_COMPLETED"
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.PropertyName == nameof(UpsertGuestProfileRequest.GenderCode));
        Assert.Contains(result.Errors, x => x.PropertyName == nameof(UpsertGuestProfileRequest.DateOfBirth));
        Assert.Contains(result.Errors, x => x.PropertyName == nameof(UpsertGuestProfileRequest.HeightCm));
        Assert.Contains(result.Errors, x => x.PropertyName == nameof(UpsertGuestProfileRequest.WeightKg));
        Assert.Contains(result.Errors, x => x.PropertyName == nameof(UpsertGuestProfileRequest.GoalCode));
        Assert.Contains(result.Errors, x => x.PropertyName == nameof(UpsertGuestProfileRequest.DailyRoutineCode));
        Assert.Contains(result.Errors, x => x.PropertyName == nameof(UpsertGuestProfileRequest.ActivityLevelCode));
    }

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
}
