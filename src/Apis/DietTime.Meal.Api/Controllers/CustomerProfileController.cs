using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Asp.Versioning;
using DietTime.Application;
using DietTime.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DietTime.Meal.Api.Controllers;

[ApiController]
[ApiVersion(1)]
[Authorize]
[Route("api/v{version:apiVersion}/customer/profile")]
public sealed class CustomerProfileController(
    ICustomerProfileService profiles) : ControllerBase
{
    /// <summary>Gets the authenticated customer's profile.</summary>
    /// <remarks>
    /// Returns only the profile belonging to the user identified by the access token.
    /// Historical nutrition targets are not returned.
    /// </remarks>
    /// <response code="200">The complete customer profile.</response>
    /// <response code="401">The access token is absent, invalid, or has no valid user ID.</response>
    /// <response code="404">The authenticated customer has not created a profile.</response>
    [HttpGet]
    [Produces("application/json")]
    [ProducesResponseType(typeof(ApiResponse<CustomerProfileResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var profile = await profiles.GetAsync(userId, ct);
        return profile is null
            ? NotFound(new ApiResponse<object>
            {
                Errors = [new("profile_not_found", "A customer profile has not been created.")]
            })
            : Ok(ApiResponse<CustomerProfileResponse>.Ok(profile));
    }

    /// <summary>Creates or updates the authenticated customer's profile.</summary>
    /// <remarks>
    /// Uses replace-all semantics for preferences and allergens. Missing or null collections
    /// are treated as empty collections.
    ///
    /// Partial onboarding example:
    /// <code>
    /// {
    ///   "genderCode": "MALE",
    ///   "preferredLanguage": "en",
    ///   "onboardingStatus": "IN_PROGRESS",
    ///   "preferences": [],
    ///   "allergens": []
    /// }
    /// </code>
    ///
    /// Completion example:
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
    ///   "onboardingStatus": "COMPLETED",
    ///   "preferences": [],
    ///   "allergens": []
    /// }
    /// </code>
    /// </remarks>
    /// <response code="200">The complete saved profile.</response>
    /// <response code="400">Validation failed or an allergen ID is inactive or unknown.</response>
    /// <response code="401">The access token is absent, invalid, or has no valid user ID.</response>
    /// <response code="409">A concurrent update changed the profile first.</response>
    [HttpPut]
    [Consumes("application/json")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(ApiResponse<CustomerProfileResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Upsert(
        UpsertCustomerProfileRequest request,
        CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var result = await profiles.UpsertAsync(userId, request, ct);
        if (!result.IsSuccess)
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

        return Ok(ApiResponse<CustomerProfileResponse>.Ok(result.Profile!));
    }

    /// <summary>Updates only the authenticated customer's preferred name.</summary>
    [HttpPatch("preferred-name")]
    [Consumes("application/json")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(ApiResponse<CustomerProfileResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdatePreferredName(
        UpdateCustomerPreferredNameRequest request,
        CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var profile = await profiles.UpdatePreferredNameAsync(
            userId,
            request.PreferredName,
            ct);
        return Ok(ApiResponse<CustomerProfileResponse>.Ok(profile));
    }

    private bool TryGetUserId(out Guid userId)
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? User.FindFirstValue("sub");
        return Guid.TryParse(value, out userId);
    }
}
