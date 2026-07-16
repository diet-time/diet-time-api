using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DietTime.Infrastructure;

public sealed class JwtOptions { public const string SectionName = "Jwt"; public string Issuer { get; set; } = ""; public string Audience { get; set; } = ""; public string Key { get; set; } = ""; public int AccessTokenMinutes { get; set; } = 15; public int RefreshTokenDays { get; set; } = 30; }

public sealed class AuthService(UserManager<ApplicationUser> users, DietTimeDbContext db, IOptions<JwtOptions> options, TimeProvider clock) : IAuthService
{
    private readonly JwtOptions jwt = options.Value;
    public async Task<TokenResponse?> RegisterAsync(RegisterRequest request, CancellationToken ct) { ct.ThrowIfCancellationRequested(); var user = new ApplicationUser { UserName = request.Email, Email = request.Email, EmailConfirmed = false }; var result = await users.CreateAsync(user, request.Password); return result.Succeeded ? await IssueAsync(user, ct) : null; }
    public async Task<TokenResponse?> LoginAsync(LoginRequest request, CancellationToken ct) { var user = await users.FindByEmailAsync(request.Email); if (user is null || !await users.CheckPasswordAsync(user, request.Password)) return null; return await IssueAsync(user, ct); }
    public async Task<TokenResponse?> RefreshAsync(RefreshRequest request, CancellationToken ct)
    {
        var hash = Hash(request.RefreshToken); var token = await db.RefreshTokens.Include(x => x.User).SingleOrDefaultAsync(x => x.TokenHash == hash && x.RevokedAt == null && x.ExpiresAt > clock.GetUtcNow(), ct); if (token is null) return null; token.RevokedAt = clock.GetUtcNow(); return await IssueAsync(token.User, ct);
    }
    private async Task<TokenResponse> IssueAsync(ApplicationUser user, CancellationToken ct)
    {
        var now = clock.GetUtcNow(); var expires = now.AddMinutes(jwt.AccessTokenMinutes); var roles = await users.GetRolesAsync(user); var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, user.Id.ToString()), new(JwtRegisteredClaimNames.Email, user.Email ?? ""), new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()) }; claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)); var token = new JwtSecurityToken(jwt.Issuer, jwt.Audience, claims, now.UtcDateTime, expires.UtcDateTime, new SigningCredentials(key, SecurityAlgorithms.HmacSha256)); var rawRefresh = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)); db.RefreshTokens.Add(new RefreshToken { UserId = user.Id, TokenHash = Hash(rawRefresh), CreatedAt = now, ExpiresAt = now.AddDays(jwt.RefreshTokenDays) }); await db.SaveChangesAsync(ct); return new(new JwtSecurityTokenHandler().WriteToken(token), rawRefresh, expires);
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
