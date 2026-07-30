using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DietTime.Infrastructure;

public sealed class AuthService(UserManager<ApplicationUser> users, DietTimeDbContext db, IOptions<JwtOptions> options, TimeProvider clock) : IAuthService
{
    private readonly JwtOptions jwt = options.Value;
    public async Task<AuthSessionResponse?> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var email = request.Email.Trim();
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = false };
        var result = await users.CreateAsync(user, request.Password);
        if (!result.Succeeded) return null;

        var now = clock.GetUtcNow();
        db.UserProfiles.Add(new UserProfile
        {
            UserId = user.Id,
            FirstName = request.FirstName?.Trim() ?? "",
            LastName = request.LastName?.Trim() ?? "",
            Status = "ACTIVE",
            IsActive = true,
            IsCustomer = true,
            CreatedAt = now,
            ModifiedAt = now
        });
        await db.SaveChangesAsync(ct);
        return await IssueAsync(user, ct);
    }

    public async Task<AuthSessionResponse?> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null || !await users.CheckPasswordAsync(user, request.Password)) return null;
        return await IssueAsync(user, ct);
    }

    public async Task<AuthSessionResponse?> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var hash = Hash(refreshToken);
        var token = await db.RefreshTokens
            .Include(x => x.User)
            .SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
        if (token is null) return null;

        var now = clock.GetUtcNow();
        if (token.RevokedAt is not null)
        {
            await RevokeAllActiveAsync(token.UserId, now, ct);
            return null;
        }
        if (token.ExpiresAt <= now)
        {
            token.RevokedAt = now;
            await db.SaveChangesAsync(ct);
            return null;
        }

        token.RevokedAt = now;
        return await IssueAsync(token.User, ct);
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken ct)
    {
        var hash = Hash(refreshToken);
        var token = await db.RefreshTokens.SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
        if (token is null || token.RevokedAt is not null) return;
        token.RevokedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    public async Task<AuthUserResponse?> GetUserAsync(Guid userId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        return user is null ? null : await BuildUserAsync(user, ct);
    }

    private async Task<AuthSessionResponse> IssueAsync(ApplicationUser user, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var accessExpiresAt = now.AddMinutes(jwt.AccessTokenMinutes);
        var refreshExpiresAt = now.AddDays(jwt.RefreshTokenDays);
        var authUser = await BuildUserAsync(user, ct);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email ?? ""),
            new(ClaimTypes.Name, authUser.Name),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        claims.AddRange(authUser.Roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key));
        var token = new JwtSecurityToken(
            jwt.Issuer,
            jwt.Audience,
            claims,
            now.UtcDateTime,
            accessExpiresAt.UtcDateTime,
            new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        var rawRefresh = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = Hash(rawRefresh),
            CreatedAt = now,
            ExpiresAt = refreshExpiresAt
        });
        await db.SaveChangesAsync(ct);
        return new(
            new JwtSecurityTokenHandler().WriteToken(token),
            accessExpiresAt,
            rawRefresh,
            refreshExpiresAt,
            authUser);
    }

    private async Task<AuthUserResponse> BuildUserAsync(ApplicationUser user, CancellationToken ct)
    {
        var profile = await db.UserProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserId == user.Id, ct);
        var roles = await users.GetRolesAsync(user);
        var name = profile?.FullName;
        if (string.IsNullOrWhiteSpace(name))
            name = user.Email?.Split('@')[0] ?? "Diet Time User";
        return new(user.Id, user.Email ?? "", name, roles.ToArray());
    }

    private async Task RevokeAllActiveAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var activeTokens = await db.RefreshTokens
            .Where(x => x.UserId == userId && x.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var activeToken in activeTokens) activeToken.RevokedAt = now;
        await db.SaveChangesAsync(ct);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
