using System.Security.Claims;
using System.Text.Encodings.Web;
using DietTime.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace DietTime.Meal.Api.Authentication;

public sealed class DevelopmentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    UserManager<ApplicationUser> users)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Development";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        Guid? userId = null;
        var displayName = "Local Development User";
        string? mobilePhone = null;

        if (Request.Headers.TryGetValue("X-Development-User-Id", out var values)
            && Guid.TryParse(values.FirstOrDefault(), out var developmentUserId))
        {
            userId = developmentUserId;
        }
        else if (Request.Headers.TryGetValue("X-Temporary-Customer-Phone", out var phoneValues))
        {
            var digits = new string((phoneValues.FirstOrDefault() ?? "")
                .Where(char.IsDigit)
                .ToArray());
            if (digits.Length is >= 7 and <= 15)
            {
                mobilePhone = $"+{digits}";
                var userName = $"mobile+{digits}@diettime.local";
                var user = await users.FindByNameAsync(userName);
                if (user is null)
                {
                    user = new ApplicationUser
                    {
                        UserName = userName,
                        Email = userName,
                        PhoneNumber = mobilePhone,
                        PhoneNumberConfirmed = true
                    };
                    var result = await users.CreateAsync(user);
                    if (!result.Succeeded)
                        return AuthenticateResult.Fail("The temporary customer could not be created.");
                }

                userId = user.Id;
                displayName = "Temporary Mobile Customer";
            }
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, displayName),
            new(ClaimTypes.Role, "Admin"),
            new(ClaimTypes.Role, "Dietitian"),
            new(ClaimTypes.Role, "ContentManager")
        };
        if (userId.HasValue)
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()));
        if (mobilePhone is not null)
            claims.Add(new Claim(ClaimTypes.MobilePhone, mobilePhone));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return AuthenticateResult.Success(ticket);
    }
}
