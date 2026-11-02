using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed class AdminCustomerAuthorizationTests
{
    [Fact]
    public async Task Customer_directory_rejects_anonymous_and_non_Admin_accounts()
    {
        const string key = "test-only-key-at-least-thirty-two-characters-long";
        await using var factory = new ApiFactory("Host=127.0.0.1;Port=54329;Database=unused;Username=unused;Password=unused;Timeout=1", services =>
        {
            services.RemoveAll<IHostedService>();
            services.PostConfigure<AuthenticationOptions>(o => { o.DefaultScheme = "Bearer"; o.DefaultChallengeScheme = "Bearer"; });
            services.PostConfigure<JwtBearerOptions>("Bearer", o =>
            {
                o.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
                o.TokenValidationParameters.ValidIssuer = "DietTime.Tests"; o.TokenValidationParameters.ValidAudience = "DietTime.Tests";
            });
        });
        using var client = factory.CreateClient();
        var paths = new[] { "/api/admin/customers", $"/api/admin/customers/{Guid.NewGuid()}", $"/api/admin/customers/{Guid.NewGuid()}/orders" };
        foreach (var path in paths) Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        foreach (var role in new[] { "Customer", "Operations", "Viewer" })
        {
            var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("DietTime.Tests", "DietTime.Tests",
                [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, role)],
                expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256)));
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            foreach (var path in paths) Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
        }
    }
}
