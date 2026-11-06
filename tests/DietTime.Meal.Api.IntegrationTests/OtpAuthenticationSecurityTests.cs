using DietTime.Application;
using DietTime.Domain;
using DietTime.Infrastructure;
using DietTime.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed class OtpPostgresFactAttribute : FactAttribute
{
    public OtpPostgresFactAttribute() {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTP_TEST_CONNECTION")))
            Skip = "Set OTP_TEST_CONNECTION to an isolated PostgreSQL instance.";
    }
}

public sealed class OtpAuthenticationSecurityTests : IAsyncLifetime
{
    private const string Phone = "+97455555555";
    private readonly string? admin = Environment.GetEnvironmentVariable("OTP_TEST_CONNECTION");
    private readonly string database = "diettime_otp_test_" + Guid.NewGuid().ToString("N");
    private string connection = "";
    private DietTimeDbContext db = null!;
    private readonly Clock clock = new();
    private readonly OtpAuthenticationOptions options = new() {
        IsTestAuthentication = true, TestOtp = "123456", AllowedTestNumbers = new HashSet<string> { Phone }
    };
    private readonly Provider live = new();
    public async Task InitializeAsync() {
        if (admin == null) return;
        await using var c = new NpgsqlConnection(admin); await c.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {database}", c); await command.ExecuteNonQueryAsync();
        connection = new NpgsqlConnectionStringBuilder(admin) { Database = database }.ConnectionString;
        db = Context();
        await ApplySchema();
    }
    public async Task DisposeAsync() {
        if (admin == null) return;
        await db.DisposeAsync(); NpgsqlConnection.ClearAllPools();
        await using var c = new NpgsqlConnection(admin); await c.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", c); await command.ExecuteNonQueryAsync();
    }
    private DietTimeDbContext Context() => new(new DbContextOptionsBuilder<DietTimeDbContext>().UseNpgsql(connection).UseSnakeCaseNamingConvention().Options);
    private PhoneOtpAuthenticationService Test(DietTimeDbContext? context = null) => new(context ?? db, new TestOtpAuthenticationProvider(options), options, clock);
    private PhoneOtpAuthenticationService Live(DietTimeDbContext? context = null) => new(context ?? db, live, new OtpAuthenticationOptions(), clock);
    private async Task ApplySchema() {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "DietTime.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var readme = await File.ReadAllTextAsync(Path.Combine(root!.FullName, "README.md"));
        var schema = System.Text.RegularExpressions.Regex.Match(readme,
            @"<!-- otp-schema:start -->\s*```sql\s*(.*?)\s*```\s*<!-- otp-schema:end -->",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(schema.Success, "The documented manual OTP schema SQL must be present.");
        await db.Database.ExecuteSqlRawAsync(schema.Groups[1].Value);
    }
    [OtpPostgresFact]
    public async Task Test_challenges_require_send_are_allowlisted_and_do_not_expose_codes() {
        Assert.Equal(PhoneOtpVerificationStatus.Invalid, await Test().VerifyAsync(Phone, "123456", default));
        Assert.Equal(400, (await Test().SendAsync("+97455555556", default)).StatusCode);
        Assert.Equal(PhoneOtpVerificationStatus.Invalid, await Test().VerifyAsync("+97455555556", "123456", default));
        Assert.Empty(await db.Set<PhoneOtpChallenge>().ToListAsync());
        var sent = await Test().SendAsync("00974 (5555) 5555", default);
        Assert.Equal(200, sent.StatusCode); Assert.Equal(300, sent.ExpiresInSeconds); Assert.Equal(60, sent.ResendAfterSeconds);
        Assert.DoesNotContain("123456", JsonSerializer.Serialize(sent));
        Assert.Equal(PhoneOtpVerificationStatus.Valid, await Test().VerifyAsync(Phone, "123456", default));
    }

    [OtpPostgresFact]
    public async Task Wrong_codes_lock_after_five_attempts_and_resending_cannot_reset_a_locked_challenge() {
        await Test().SendAsync(Phone, default);
        for (var i = 0; i < 5; i++) Assert.Equal(PhoneOtpVerificationStatus.Invalid, await Test().VerifyAsync(Phone, "654321", default));
        Assert.Equal(PhoneOtpVerificationStatus.TooManyAttempts, await Test().VerifyAsync(Phone, "123456", default));
        clock.Now = clock.Now.AddSeconds(60);
        Assert.Equal(429, (await Test().SendAsync(Phone, default)).StatusCode);
        clock.Now = clock.Now.AddSeconds(241);
        Assert.Equal(200, (await Test().SendAsync(Phone, default)).StatusCode);
        Assert.Equal(PhoneOtpVerificationStatus.Valid, await Test().VerifyAsync(Phone, "123456", default));
    }

    [OtpPostgresFact]
    public async Task Expiration_is_exactly_five_minutes() {
        await Test().SendAsync(Phone, default); clock.Now = clock.Now.AddSeconds(300);
        Assert.Equal(PhoneOtpVerificationStatus.Invalid, await Test().VerifyAsync(Phone, "123456", default));
    }

    [OtpPostgresFact]
    public async Task Successful_challenge_cannot_be_replayed() {
        await Test().SendAsync(Phone, default);
        Assert.Equal(PhoneOtpVerificationStatus.Valid, await Test().VerifyAsync(Phone, "123456", default));
        await using var other = Context();
        Assert.Equal(PhoneOtpVerificationStatus.Invalid, await Test(other).VerifyAsync(Phone, "123456", default));
    }

    [OtpPostgresFact]
    public async Task Concurrent_verification_in_separate_contexts_consumes_once() {
        await Test().SendAsync(Phone, default);
        async Task<PhoneOtpVerificationStatus> Verify() { await using var c = Context(); return await Test(c).VerifyAsync(Phone, "123456", default); }
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Verify()));
        Assert.Single(results.Where(x => x == PhoneOtpVerificationStatus.Valid));
        Assert.Equal(7, results.Count(x => x == PhoneOtpVerificationStatus.Invalid));
    }

    [OtpPostgresFact]
    public async Task Concurrent_sends_only_issue_one_challenge() {
        async Task<PhoneOtpSendResult> Send() { await using var c = Context(); return await Test(c).SendAsync(Phone, default); }
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Send()));
        Assert.Single(results.Where(x => x.StatusCode == 200)); Assert.Equal(7, results.Count(x => x.StatusCode == 429));
    }

    [OtpPostgresFact]
    public async Task Cooldown_is_server_enforced_and_phone_rate_window_survives_new_challenges() {
        Assert.Equal(200, (await Test().SendAsync(Phone, default)).StatusCode);
        clock.Now = clock.Now.AddSeconds(59);
        Assert.Equal(1, (await Test().SendAsync(Phone, default)).RetryAfterSeconds);
        clock.Now = clock.Now.AddSeconds(1);
        for (var i = 0; i < 4; i++) { Assert.Equal(200, (await Test().SendAsync(Phone, default)).StatusCode); clock.Now = clock.Now.AddSeconds(60); }
        Assert.Equal(429, (await Test().SendAsync(Phone, default)).StatusCode);
        clock.Now = clock.Now.AddMinutes(15);
        Assert.Equal(200, (await Test().SendAsync(Phone, default)).StatusCode);
    }

    [OtpPostgresFact]
    public async Task Live_provider_validates_issued_code_and_does_not_accept_test_code() {
        Assert.Equal(200, (await Live().SendAsync(Phone, default)).StatusCode);
        Assert.Equal(PhoneOtpVerificationStatus.Invalid, await Live().VerifyAsync(Phone, "123456", default));
        Assert.Equal(PhoneOtpVerificationStatus.Valid, await Live().VerifyAsync(Phone, "482913", default));
        Assert.Equal(PhoneOtpVerificationStatus.Invalid, await Live().VerifyAsync(Phone, "482913", default));
        Assert.Equal(2, live.Verifications);
    }

    [OtpPostgresFact]
    public async Task Live_delivery_failure_never_falls_back_to_test_authentication() {
        live.FailSend = true;
        Assert.Equal(503, (await Live().SendAsync(Phone, default)).StatusCode);
        Assert.Equal(PhoneOtpVerificationStatus.Invalid, await Live().VerifyAsync(Phone, "123456", default));
        Assert.Equal(0, live.Verifications);
    }

    [OtpPostgresFact]
    public async Task Live_resends_keep_original_expiry_and_attempts_then_cancel_expired_provider_session() {
        await Live().SendAsync(Phone, default);
        await Live().VerifyAsync(Phone, "654321", default);
        clock.Now = clock.Now.AddSeconds(60);
        Assert.Equal(240, (await Live().SendAsync(Phone, default)).ExpiresInSeconds);
        Assert.Equal(1, (await db.Set<PhoneOtpChallenge>().SingleAsync()).VerificationAttempts);
        clock.Now = clock.Now.AddSeconds(240);
        Assert.Equal(PhoneOtpVerificationStatus.Invalid, await Live().VerifyAsync(Phone, "482913", default));
        Assert.Equal(300, (await Live().SendAsync(Phone, default)).ExpiresInSeconds);
        Assert.Equal(1, live.Cancellations);
    }

    [OtpPostgresFact]
    public async Task Expiry_is_rechecked_after_the_provider_response() {
        await Live().SendAsync(Phone, default);
        live.DuringVerify = () => clock.Now = clock.Now.AddMinutes(6);
        Assert.Equal(PhoneOtpVerificationStatus.Invalid, await Live().VerifyAsync(Phone, "482913", default));
        Assert.NotNull((await db.Set<PhoneOtpChallenge>().SingleAsync()).ConsumedAt);
    }

    [OtpPostgresFact]
    public async Task Manual_schema_setup_is_idempotent_and_preserves_metadata_without_storing_OTP_values() {
        await Test().SendAsync(Phone, default); await ApplySchema();
        Assert.Single(await db.Set<PhoneOtpChallenge>().ToListAsync());
        var properties = db.Model.FindEntityType(typeof(PhoneOtpChallenge))!.GetProperties().Select(p => p.Name);
        Assert.DoesNotContain(properties, p => p.Contains("Hash") || p.Contains("Otp") || p.Contains("Code"));
    }

    [OtpPostgresFact]
    public async Task Existing_JWT_login_reuses_account_and_profiles_after_a_new_challenge() {
        clock.Now = DateTimeOffset.UtcNow;
        // Only this test needs the application tables; metadata-only tests above run the documented manual schema SQL.
        await db.Database.EnsureDeletedAsync(); await db.Database.EnsureCreatedAsync();
        await using var factory = new ApiFactory(connection, services => {
            services.RemoveAll<IHostedService>(); services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock);
        });
        using var client = factory.CreateClient();
        using var providerScope = factory.Services.CreateScope();
        Assert.IsType<TestOtpAuthenticationProvider>(providerScope.ServiceProvider.GetRequiredService<IPhoneOtpProvider>());
        async Task<JsonElement> Login() {
            var send = await client.PostAsJsonAsync("/api/v1/auth/phone-otp/request", new { phoneNumber = Phone });
            Assert.Equal(HttpStatusCode.OK, send.StatusCode);
            var response = await client.PostAsJsonAsync("/api/v1/auth/phone-otp", new { phoneNumber = Phone, otp = "123456" });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("data").Clone();
        }
        var first = await Login(); var user = first.GetProperty("user").GetProperty("id").GetString();
        var profileCount = await db.CustomerProfiles.CountAsync();
        var userProfileCount = await db.UserProfiles.CountAsync();
        clock.Now = DateTimeOffset.UtcNow;
        await db.Set<PhoneOtpChallenge>().ExecuteUpdateAsync(s => s.SetProperty(c => c.NextSendAt, clock.Now.AddSeconds(-1)));
        var second = await Login(); Assert.Equal(user, second.GetProperty("user").GetProperty("id").GetString());
        Assert.Equal(profileCount, await db.CustomerProfiles.CountAsync()); Assert.Equal(userProfileCount, await db.UserProfiles.CountAsync());
        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(second.GetProperty("accessToken").GetString());
        var validation = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>>().Get("Bearer").TokenValidationParameters;
        Assert.Equal(jwt.Issuer, validation.ValidIssuer);
        Assert.Equal(jwt.Audiences.Single(), validation.ValidAudience);
        client.DefaultRequestHeaders.Authorization = new("Bearer", second.GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [OtpPostgresFact]
    public async Task Request_limits_are_partitioned_by_remote_IP_not_global()
    {
        await db.Database.EnsureDeletedAsync(); await db.Database.EnsureCreatedAsync();
        await using var factory = new ApiFactory(connection, services => {
            services.RemoveAll<IHostedService>();
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, RemoteIpFilter>();
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Test-Remote-IP", "192.0.2.1");
        for (var i = 0; i < 20; i++)
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/auth/phone-otp/request", new { phoneNumber = "invalid" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/v1/auth/phone-otp/request", new { phoneNumber = "invalid" })).StatusCode);
        client.DefaultRequestHeaders.Remove("Test-Remote-IP"); client.DefaultRequestHeaders.Add("Test-Remote-IP", "192.0.2.2");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/auth/phone-otp/request", new { phoneNumber = "invalid" })).StatusCode);
    }

    private sealed class RemoteIpFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app => {
            app.Use(nextRequest => context => {
                context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers["Test-Remote-IP"].FirstOrDefault() ?? "192.0.2.1");
                return nextRequest(context);
            });
            next(app);
        };
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Provider : IPhoneOtpProvider
    {
        private string? id;
        public bool FailSend; public int Verifications, Cancellations; public Action? DuringVerify;
        public Task<PhoneOtpSendResult> SendAsync(string phone, CancellationToken ct) => Task.FromResult(FailSend ? new PhoneOtpSendResult(503) : new PhoneOtpSendResult(200, id ??= "VE" + Guid.NewGuid().ToString("N")));
        public Task<bool> CancelAsync(string requestId, CancellationToken ct) { Cancellations++; id = null; return Task.FromResult(true); }
        public Task<PhoneOtpVerificationStatus> VerifyAsync(string phone, string otp, CancellationToken ct) { Verifications++; DuringVerify?.Invoke(); return Task.FromResult(otp == "482913" ? PhoneOtpVerificationStatus.Valid : PhoneOtpVerificationStatus.Invalid); }
    }
}
