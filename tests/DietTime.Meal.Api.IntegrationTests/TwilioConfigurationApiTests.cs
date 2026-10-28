using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using DietTime.Application;
using DietTime.Infrastructure;
using DietTime.Meal.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed class TwilioConfigurationApiTests
{
    private const string Path = "/api/v1/admin/integrations/whatsapp/twilio/configuration";
    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("Customer", HttpStatusCode.Forbidden)]
    [InlineData("Admin", HttpStatusCode.OK)]
    public async Task Runtime_diagnostics_are_admin_only_do_not_expose_values_and_never_send(string? role, HttpStatusCode expected)
    {
        var noSend = new NeverSend();
        await using var factory = Factory(noSend, true);
        using var client = factory.CreateClient();
        if (role != null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(role));
        var response = await client.GetAsync(Path);
        Assert.Equal(expected, response.StatusCode); Assert.Equal(0, noSend.Calls);
        if (expected != HttpStatusCode.OK) return;
        var text = await response.Content.ReadAsStringAsync();
        var data = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.True(data.GetProperty("enabled").GetBoolean()); Assert.True(data.GetProperty("configurationComplete").GetBoolean());
        Assert.False(data.GetProperty("providerVerified").GetBoolean());
        Assert.Empty(data.GetProperty("invalidOrMissingSettings").EnumerateArray());
        Assert.DoesNotContain("private-token", text); Assert.DoesNotContain("+14155238886", text);
        Assert.DoesNotContain("+97455555555", text); Assert.DoesNotContain("AC" + new string('1',32), text); Assert.DoesNotContain("HX" + new string('2',32), text);
    }

    [Fact]
    public async Task Disabled_configuration_reports_what_the_running_process_actually_loaded()
    {
        var noSend = new NeverSend(); await using var factory = Factory(noSend, false);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("Admin"));
        var response = await client.GetAsync(Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.False(data.GetProperty("enabled").GetBoolean()); Assert.False(data.GetProperty("configurationComplete").GetBoolean());
        Assert.Equal(5, data.GetProperty("invalidOrMissingSettings").GetArrayLength()); Assert.Equal(0, noSend.Calls);
    }

    private static WebApplicationFactory<Program> Factory(NeverSend noSend, bool enabled) => new ApiFactory("Host=localhost;Database=unused;Username=unused;Password=unused", services => {
        var worker = services.FirstOrDefault(d => d.ImplementationType == typeof(TomorrowMealFinalizationWorker));
        if (worker != null) services.Remove(worker);
        services.AddSingleton<ITwilioWhatsAppService>(noSend);
        services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o => {
            o.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("test-only-key-at-least-thirty-two-characters-long"));
            o.TokenValidationParameters.ValidIssuer = "DietTime.Tests"; o.TokenValidationParameters.ValidAudience = "DietTime.Tests";
        });
        services.PostConfigure<TwilioWhatsAppOptions>(o => {
            o.Enabled = enabled; o.AccountSid = enabled ? "AC" + new string('1',32) : ""; o.AuthToken = enabled ? "private-token" : "";
            o.FromNumber = enabled ? "+14155238886" : ""; o.OperationsNumber = enabled ? "+97455555555" : ""; o.NewOrderContentSid = enabled ? "HX" + new string('2',32) : "";
        });
    }).WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));

    private static string Token(string role) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("DietTime.Tests", "DietTime.Tests",
        [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, role)],
        expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("test-only-key-at-least-thirty-two-characters-long")), SecurityAlgorithms.HmacSha256)));

    private sealed class NeverSend : ITwilioWhatsAppService
    {
        public int Calls { get; private set; }
        public Task<WhatsAppSendResult> SendTemplateAsync(TwilioWhatsAppTemplateMessage message, CancellationToken ct = default) { Calls++; throw new InvalidOperationException("Diagnostics must not send"); }
        public Task<WhatsAppSendResult> SendNewOrderNotificationAsync(NewOrderWhatsAppNotification notification, CancellationToken ct = default) { Calls++; throw new InvalidOperationException("Diagnostics must not send"); }
    }
}
