using Asp.Versioning;
using System.Security.Claims;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Meal.Api.Controllers;

[ApiController]
[ApiVersion("1")]
[Route("api/v{version:apiVersion}/auth")]
public class AuthenticationController(
    IAuthService authService,
    IPasswordService passwordService,
    IEmailService emailService,
    UserManager<ApplicationUser> userManager,
    IUserProfileService userProfileService,
    DietTimeDbContext context,
    IWebHostEnvironment environment) : ControllerBase
{
    private const string RefreshCookieName = "diet_time_refresh";

    /// <summary>
    /// Register a customer account and establish a session.
    /// </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<AuthSessionResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request, CancellationToken cancellationToken)
    {
        var session = await authService.RegisterAsync(request, cancellationToken);
        if (session is null)
            return Conflict(new ProblemDetails { Title = "Registration failed", Detail = "An account may already exist or the password does not meet the policy." });
        SetRefreshCookie(session);
        return Ok(ApiResponse<AuthSessionResponse>.Ok(session));
    }

    /// <summary>
    /// Login with email and password and establish a rotating session.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<AuthSessionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new ProblemDetails { Title = "Validation Error", Detail = "Email and password are required" });

        var session = await authService.LoginAsync(request, cancellationToken);
        if (session is null)
            return Unauthorized(new ProblemDetails { Title = "Authentication Failed", Detail = "Invalid email or password" });
        SetRefreshCookie(session);
        return Ok(ApiResponse<AuthSessionResponse>.Ok(session));
    }

    /// <summary>
    /// Rotate the refresh token and return a fresh access token.
    /// </summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<AuthSessionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest? request, CancellationToken cancellationToken)
    {
        var refreshToken = ResolveRefreshToken(request?.RefreshToken);
        if (refreshToken is null)
            return Unauthorized(new ProblemDetails { Title = "Invalid session", Detail = "A refresh token is required." });
        var session = await authService.RefreshAsync(refreshToken, cancellationToken);
        if (session is null)
        {
            ClearRefreshCookie();
            return Unauthorized(new ProblemDetails { Title = "Invalid session", Detail = "The session has expired or was revoked." });
        }
        SetRefreshCookie(session);
        return Ok(ApiResponse<AuthSessionResponse>.Ok(session));
    }

    /// <summary>
    /// Revoke the current refresh session and clear its browser cookie.
    /// </summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest? request, CancellationToken cancellationToken)
    {
        var refreshToken = ResolveRefreshToken(request?.RefreshToken);
        if (refreshToken is not null)
            await authService.RevokeAsync(refreshToken, cancellationToken);
        ClearRefreshCookie();
        return NoContent();
    }

    /// <summary>
    /// Return the user represented by the current access token.
    /// </summary>
    [HttpGet("me")]
    [Authorize(AuthenticationSchemes = "Bearer")]
    [ProducesResponseType(typeof(ApiResponse<AuthUserResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        var rawUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(rawUserId, out var userId)) return Unauthorized();
        var user = await authService.GetUserAsync(userId, cancellationToken);
        return user is null ? Unauthorized() : Ok(ApiResponse<AuthUserResponse>.Ok(user));
    }

    /// <summary>
    /// Request a password reset link via email
    /// </summary>
    /// <remarks>
    /// Sends an email with a password reset link valid for 24 hours.
    /// User clicks the link and is directed to set a new password.
    /// </remarks>
    [HttpPost("request-password-reset")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(RequestPasswordResetResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RequestPasswordReset([FromBody] RequestPasswordResetRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
            return BadRequest(new ProblemDetails { Title = "Validation Error", Detail = "Email is required" });

        var user = await userManager.FindByEmailAsync(request.Email);
        if (user == null)
        {
            // Don't reveal whether user exists for security reasons
            return Ok(new RequestPasswordResetResponse(
                "If an account exists with this email, a password reset link has been sent.",
                true));
        }

        try
        {
            var userProfile = await userProfileService.GetByUserIdAsync(user.Id, cancellationToken);
            var resetToken = await passwordService.GeneratePasswordResetTokenAsync(user.Id, cancellationToken);
            
            // Format reset URL with token
            var resetUrl = $"https://localhost:5173/auth/set-password?token={resetToken}";
            
            // Send email
            var emailSent = await emailService.SendPasswordResetEmailAsync(
                user.Email!,
                userProfile?.FirstName ?? "User",
                resetUrl,
                cancellationToken);
            
            if (!emailSent)
            {
                // Log but don't reveal to user for security
                // In production, you might want to retry or alert admin
            }
            
            return Ok(new RequestPasswordResetResponse(
                "If an account exists with this email, a password reset link has been sent.",
                true));
        }
        catch (Exception ex)
        {
            // Log the error
            HttpContext.Items["ExceptionMessage"] = ex.Message;
            return Ok(new RequestPasswordResetResponse(
                "If an account exists with this email, a password reset link has been sent.",
                true));
        }
    }

    /// <summary>
    /// Set a new password using the reset token
    /// </summary>
    /// <remarks>
    /// This endpoint is called after user clicks the reset link in their email.
    /// The reset token is valid for 24 hours.
    /// </remarks>
    [HttpPost("set-password")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(SetPasswordResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SetPassword([FromBody] SetPasswordRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ResetToken))
            return BadRequest(new ProblemDetails { Title = "Validation Error", Detail = "Reset token is required" });

        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 8)
            return BadRequest(new ProblemDetails { Title = "Validation Error", Detail = "Password must be at least 8 characters long" });

        // Find the user with this reset token
        var userAttribute = await context.UserAttributes
            .Where(ua => ua.Key == "PWDGEN" && ua.Value == request.ResetToken)
            .FirstOrDefaultAsync(cancellationToken);

        if (userAttribute == null)
            return Unauthorized(new ProblemDetails { Title = "Invalid Token", Detail = "Reset token not found or expired" });

        // Validate token hasn't expired (24 hours)
        var tokenAge = DateTimeOffset.UtcNow - userAttribute.UpdatedAt;
        if (tokenAge.TotalHours > 24)
        {
            context.UserAttributes.Remove(userAttribute);
            await context.SaveChangesAsync(cancellationToken);
            return Unauthorized(new ProblemDetails { Title = "Token Expired", Detail = "Reset token has expired. Please request a new one." });
        }

        try
        {
            // Set new password (which also clears the reset token)
            var success = await passwordService.SetPasswordWithResetTokenAsync(
                userAttribute.UserId,
                request.ResetToken,
                request.NewPassword,
                cancellationToken);

            if (!success)
                return BadRequest(new ProblemDetails { Title = "Operation Failed", Detail = "Failed to set new password" });

            return Ok(new SetPasswordResponse(
                "Password has been successfully reset. You can now login with your new password.",
                true));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError,
                new ProblemDetails { Title = "System Error", Detail = "An error occurred while setting your password" });
        }
    }

    private string? ResolveRefreshToken(string? bodyToken)
    {
        if (!string.IsNullOrWhiteSpace(bodyToken)) return bodyToken;
        return Request.Cookies.TryGetValue(RefreshCookieName, out var cookieToken) &&
               !string.IsNullOrWhiteSpace(cookieToken)
            ? cookieToken
            : null;
    }

    private void SetRefreshCookie(AuthSessionResponse session) =>
        Response.Cookies.Append(RefreshCookieName, session.RefreshToken, CookieOptions(session.RefreshTokenExpiresAt));

    private void ClearRefreshCookie() =>
        Response.Cookies.Delete(RefreshCookieName, CookieOptions(DateTimeOffset.UnixEpoch));

    private CookieOptions CookieOptions(DateTimeOffset expires) => new()
    {
        HttpOnly = true,
        Secure = !environment.IsDevelopment(),
        SameSite = SameSiteMode.Strict,
        Path = "/api/v1/auth",
        Expires = expires,
        IsEssential = true
    };
}
