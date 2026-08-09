using System.Reflection;
using System.Net;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Meal.Api.Controllers;
using DietTime.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed class AuthenticationContractTests
{
    [Fact]
    public void Customer_purchase_contract_exposes_routes_without_recommendation_fields()
    {
        var route = Assert.Single(typeof(CustomerMealPlansController)
            .GetCustomAttributes<Microsoft.AspNetCore.Mvc.RouteAttribute>());
        Assert.Equal("api/v{version:apiVersion}/customer/meal-plans", route.Template);

        var actions = typeof(CustomerMealPlansController).GetMethods(BindingFlags.Instance | BindingFlags.Public);
        Assert.Contains(actions, method => method.GetCustomAttributes<HttpGetAttribute>()
            .Any(attribute => attribute.Template == "{mealPlanCode}/purchase-options"));
        Assert.Contains(actions, method => method.GetCustomAttributes<HttpPostAttribute>()
            .Any(attribute => attribute.Template == "validate-selection"));

        var responseProperties = typeof(MealPlanPurchaseOptionsResponse)
            .Assembly
            .GetTypes()
            .Where(type => type.Name.StartsWith("MealPlanPurchase", StringComparison.Ordinal)
                || type == typeof(MealPlanMealConfigurationResponse))
            .SelectMany(type => type.GetProperties())
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain(responseProperties, property =>
            property.Contains("recommend", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Customer_purchase_service_is_registered()
    {
        using var factory = new ApiFactory(
            "Host=localhost;Database=registration_check;Username=postgres;Password=unused");
        using var scope = factory.Services.CreateScope();

        Assert.IsType<CustomerMealPlanPurchaseService>(
            scope.ServiceProvider.GetRequiredService<ICustomerMealPlanPurchaseService>());
    }

    [Fact]
    public void Authentication_routes_are_unique_and_complete()
    {
        var routes = typeof(Program).Assembly
            .GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type))
            .SelectMany(type =>
            {
                var prefixes = type.GetCustomAttributes<RouteAttribute>()
                    .Select(attribute => attribute.Template)
                    .Where(template => template.Contains("/auth", StringComparison.OrdinalIgnoreCase));
                return type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .SelectMany(method => method.GetCustomAttributes<HttpMethodAttribute>())
                    .SelectMany(attribute => prefixes.Select(prefix =>
                        $"{attribute.HttpMethods.Single()} {prefix}/{attribute.Template}"));
            })
            .ToArray();

        Assert.Equal(routes.Length, routes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(routes, route => route.EndsWith("/register", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(routes, route => route.EndsWith("/login", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(routes, route => route.EndsWith("/phone-otp", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(routes, route => route.EndsWith("/refresh", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(routes, route => route.EndsWith("/logout", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(routes, route => route.EndsWith("/me", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Phone_otp_contract_supports_first_use_profile_details()
    {
        var request = new PhoneOtpLoginRequest("+97455555555", "123456", "Test", "User");

        Assert.Equal("+97455555555", request.PhoneNumber);
        Assert.Equal("123456", request.Otp);
        Assert.Equal("Test", request.FirstName);
        Assert.Equal("User", request.LastName);
    }

    [Fact]
    public void Session_contract_carries_user_roles_and_both_expirations()
    {
        var now = DateTimeOffset.UtcNow;
        var response = new AuthSessionResponse(
            "access",
            now.AddMinutes(15),
            "refresh",
            now.AddDays(7),
            new AuthUserResponse(Guid.NewGuid(), "admin@example.test", "Admin", ["Admin"]));

        Assert.Equal("access", response.AccessToken);
        Assert.Equal("refresh", response.RefreshToken);
        Assert.True(response.AccessTokenExpiresAt < response.RefreshTokenExpiresAt);
        Assert.Contains("Admin", response.User.Roles);
    }

    [Fact]
    public void Refresh_session_migration_targets_the_identity_user_table()
    {
        var options = new DbContextOptionsBuilder<DietTimeDbContext>()
            .UseNpgsql("Host=localhost;Database=contract_check;Username=postgres;Password=unused")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var context = new DietTimeDbContext(options);

        Assert.Equal(
            "AspNetUsers",
            context.Model.FindEntityType(typeof(ApplicationUser))?.GetTableName());
    }

    [Fact]
    public async Task Swagger_document_generates_with_multipart_upload_operations()
    {
        await using var factory = new ApiFactory(
            "Host=localhost;Database=swagger_check;Username=postgres;Password=unused");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/swagger/v1/swagger.json");
        var document = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("/api/v1/admin/meals/{mealId}/media/upload", document);
        Assert.Contains("/api/v1/admin/meal-plans/{planId}/image/upload", document);
    }

    [Theory]
    [InlineData("Host=localhost;Database=diettime;Username=postgres;Password=test")]
    [InlineData("\"Host=localhost;Database=diettime;Username=postgres;Password=test\"")]
    public void Database_connection_resolver_accepts_npgsql_strings(string value)
    {
        var normalized = DatabaseConnectionStringResolver.Normalize(value);
        var parsed = new NpgsqlConnectionStringBuilder(normalized);

        Assert.Equal("localhost", parsed.Host);
        Assert.Equal("diettime", parsed.Database);
    }

    [Fact]
    public void Database_connection_resolver_accepts_postgres_uris()
    {
        var normalized = DatabaseConnectionStringResolver.Normalize(
            "postgresql://postgres:secret@db.example.test:5433/diettime");
        var parsed = new NpgsqlConnectionStringBuilder(normalized);

        Assert.Equal("db.example.test", parsed.Host);
        Assert.Equal(5433, parsed.Port);
        Assert.Equal("diettime", parsed.Database);
        Assert.Equal("postgres", parsed.Username);
    }
}
