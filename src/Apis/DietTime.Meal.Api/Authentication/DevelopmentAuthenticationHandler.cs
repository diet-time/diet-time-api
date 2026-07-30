using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace DietTime.Meal.Api.Authentication;

public sealed class DevelopmentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Development";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "Local Development User"),
            new(ClaimTypes.Role, "Admin"),
            new(ClaimTypes.Role, "Dietitian"),
            new(ClaimTypes.Role, "ContentManager")
        };
        if (Request.Headers.TryGetValue("X-Development-User-Id", out var values) &&
            Guid.TryParse(values.FirstOrDefault(), out var userId))
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId.ToString()));
        }
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
