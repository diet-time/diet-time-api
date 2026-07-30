using Asp.Versioning;
using DietTime.Application;
using DietTime.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DietTime.Meal.Api.Controllers;

[ApiController]
[ApiVersion(1)]
[AllowAnonymous]
[Route("api/v{version:apiVersion}/guest")]
public sealed class GuestProfileController(
    IGuestTokenGenerator tokenGenerator,
    IGuestTokenResolver tokenResolver,
    IGuestProfileService profiles) : ControllerBase
{
    /// <summary>Creates a secure anonymous guest session.</summary>
    /// <remarks>
    /// The returned token must be retained securely by the Flutter application and sent in
    /// `X-Guest-Token` for profile operations. The raw token is never stored by the API.
    /// </remarks>
    /// <response code="200">A new guest token and its expiry.</response>
    /// <response code="429">Too many guest sessions were requested.</response>
    [HttpPost("session")]
    [EnableRateLimiting("guest-session")]
    [ProducesResponseType(typeof(ApiResponse<GuestSessionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public ActionResult<ApiResponse<GuestSessionResponse>> CreateSession()
    {
        var session = tokenGenerator.Generate();
        return Ok(ApiResponse<GuestSessionResponse>.Ok(
            new(session.RawToken, session.ExpiresAt)));
    }

    /// <summary>Gets the current guest onboarding profile.</summary>
    /// <response code="200">The complete guest profile.</response>
    /// <response code="401">The guest session is invalid or expired.</response>
    /// <response code="404">No profile has been saved for this valid guest token.</response>
    /// <response code="429">The guest-profile rate limit was exceeded.</response>
    [HttpGet("profile")]
    [EnableRateLimiting("guest-profile")]
    [ProducesResponseType(typeof(ApiResponse<GuestCustomerProfileResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetProfile(CancellationToken ct)
    {
        var resolution = await ResolveAsync(requireProfile: true, ct);
        if (resolution.Status == GuestTokenResolutionStatus.Invalid)
            return InvalidSession();
        if (resolution.Status == GuestTokenResolutionStatus.ProfileNotFound)
            return ProfileNotFound();

        var profile = await profiles.GetAsync(resolution.ProfileId!.Value, ct);
        return profile is null
            ? InvalidSession()
            : Ok(ApiResponse<GuestCustomerProfileResponse>.Ok(profile));
    }

    /// <summary>Creates or updates a progressive guest onboarding profile.</summary>
    /// <remarks>
    /// Send the raw session token in `X-Guest-Token`. Preferences and allergens use replace-all
    /// semantics, so an empty list removes all saved entries.
    ///
    /// First-step example:
    /// <code>
    /// {
    ///   "genderCode": "MALE",
    ///   "dateOfBirth": "1990-06-15",
    ///   "preferredLanguage": "en",
    ///   "onboardingStatus": "IN_PROGRESS",
    ///   "preferences": [],
    ///   "allergens": []
    /// }
    /// </code>
    ///
    /// Completed profile example:
    /// <code>
    /// {
    ///   "genderCode": "MALE",
    ///   "dateOfBirth": "1990-06-15",
    ///   "heightCm": 175,
    ///   "weightKg": 82,
    ///   "goalCode": "LOSE_WEIGHT",
    ///   "dailyRoutineCode": "OFFICE_WORK",
    ///   "activityLevelCode": "LIGHT_ACTIVITY",
    ///   "preferredLanguage": "en",
    ///   "onboardingStatus": "PROFILE_COMPLETED",
    ///   "preferences": [],
    ///   "allergens": []
    /// }
    /// </code>
    /// </remarks>
    /// <response code="200">The complete saved guest profile.</response>
    /// <response code="400">Validation failed or an allergen is unknown or inactive.</response>
    /// <response code="401">The guest session is invalid or expired.</response>
    /// <response code="409">A concurrent update changed the profile first.</response>
    /// <response code="429">The guest-profile rate limit was exceeded.</response>
    [HttpPut("profile")]
    [EnableRateLimiting("guest-profile")]
    [Consumes("application/json")]
    [RequestSizeLimit(64 * 1024)]
    [ProducesResponseType(typeof(ApiResponse<GuestCustomerProfileResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> UpsertProfile(
        UpsertGuestProfileRequest request,
        CancellationToken ct)
    {
        var resolution = await ResolveAsync(requireProfile: false, ct);
        if (resolution.Status != GuestTokenResolutionStatus.Valid)
            return InvalidSession();

        var result = await profiles.UpsertAsync(resolution.TokenHash!, request, ct);
        if (result.InvalidAllergenIds.Count > 0)
        {
            return BadRequest(new ApiResponse<object>
            {
                Errors = result.InvalidAllergenIds
                    .Select(id => new ApiError(
                        "invalid_allergen_id",
                        $"Allergen '{id}' does not exist or is inactive.",
                        "allergens"))
                    .ToArray()
            });
        }

        return Ok(ApiResponse<GuestCustomerProfileResponse>.Ok(result.Profile!));
    }

    private Task<GuestTokenResolution> ResolveAsync(
        bool requireProfile,
        CancellationToken ct) =>
        tokenResolver.ResolveAsync(
            Request.Headers["X-Guest-Token"].Select(value => value ?? string.Empty).ToArray(),
            requireProfile,
            ct);

    private UnauthorizedObjectResult InvalidSession() =>
        Unauthorized(new ApiResponse<object>
        {
            Errors = [new("invalid_guest_session", "Guest session is invalid or has expired.")]
        });

    private NotFoundObjectResult ProfileNotFound() =>
        NotFound(new ApiResponse<object>
        {
            Errors = [new("guest_profile_not_found", "No guest profile has been saved.")]
        });
}

[ApiController]
[ApiVersion(1)]
[AllowAnonymous]
[Route("api/v{version:apiVersion}/guest/plan-recommendations")]
public sealed class GuestPlanRecommendationController(
    IGuestTokenResolver tokenResolver,
    IGuestPlanRecommendationService recommendations) : ControllerBase
{
    /// <summary>Gets ranked meal-plan recommendations for the current guest profile.</summary>
    /// <response code="200">Suitable active meal plans ranked by profile compatibility.</response>
    /// <response code="401">The guest session is invalid or expired.</response>
    /// <response code="404">No profile has been saved for this valid guest token.</response>
    /// <response code="429">The guest-profile rate limit was exceeded.</response>
    [HttpGet]
    [EnableRateLimiting("guest-profile")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<GuestPlanRecommendationResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var resolution = await tokenResolver.ResolveAsync(
            Request.Headers["X-Guest-Token"].Select(value => value ?? string.Empty).ToArray(),
            requireProfile: true,
            ct);
        if (resolution.Status == GuestTokenResolutionStatus.Invalid)
        {
            return Unauthorized(new ApiResponse<object>
            {
                Errors = [new("invalid_guest_session", "Guest session is invalid or has expired.")]
            });
        }
        if (resolution.Status == GuestTokenResolutionStatus.ProfileNotFound)
        {
            return NotFound(new ApiResponse<object>
            {
                Errors = [new("guest_profile_not_found", "No guest profile has been saved.")]
            });
        }

        var result = await recommendations.GetAsync(resolution.ProfileId!.Value, ct);
        return Ok(ApiResponse<IReadOnlyList<GuestPlanRecommendationResponse>>.Ok(result));
    }
}
