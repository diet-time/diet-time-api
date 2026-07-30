using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Amazon.S3;
using Asp.Versioning;
using DietTime.Meal.Api.Authentication;
using DietTime.Meal.Api.Middleware;
using DietTime.Meal.Api;
using DietTime.Application;
using DietTime.Infrastructure;
using DietTime.Persistence;
using FluentValidation;
using FluentValidation.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var maxUploadSizeBytes = builder.Configuration.GetValue<long?>("Storage:MaxUploadSizeBytes") ?? 10 * 1024 * 1024;
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = maxUploadSizeBytes + 1024 * 1024);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maxUploadSizeBytes + 1024 * 1024);
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}
builder.Host.UseSerilog((context, config) => config.ReadFrom.Configuration(context.Configuration).Enrich.FromLogContext().WriteTo.Console());

var connection = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.");
builder.Services.AddSingleton<GuestHomeCacheVersion>();
builder.Services.AddSingleton<GuestHomeCacheInvalidationInterceptor>();
builder.Services.AddDbContext<DietTimeDbContext>((services, options) => options
    .UseNpgsql(connection)
    .UseSnakeCaseNamingConvention()
    .AddInterceptors(services.GetRequiredService<GuestHomeCacheInvalidationInterceptor>()));
builder.Services.AddIdentityCore<ApplicationUser>(o => { o.Password.RequiredLength = 10; o.Password.RequireDigit = true; o.Password.RequireUppercase = true; o.Lockout.MaxFailedAccessAttempts = 5; o.SignIn.RequireConfirmedEmail = false; })
    .AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<DietTimeDbContext>().AddDefaultTokenProviders();

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.Configure<DeliveryScheduleOptions>(builder.Configuration.GetSection(DeliveryScheduleOptions.SectionName));
builder.Services.Configure<CustomerNutritionOptions>(builder.Configuration.GetSection(CustomerNutritionOptions.SectionName));
builder.Services.AddOptions<GuestProfileOptions>()
    .Bind(builder.Configuration.GetSection(GuestProfileOptions.SectionName))
    .Validate(
        options =>
            options.TokenExpiryDays > 0 &&
            options.TokenLengthBytes is >= 32 and <= 128 &&
            options.ExpiredProfileRetentionDays >= 0 &&
            options.CleanupIntervalHours > 0 &&
            options.CleanupBatchSize is >= 1 and <= 5000 &&
            (options.HashAlgorithm.Equals("SHA256", StringComparison.OrdinalIgnoreCase) ||
             options.HashAlgorithm.Equals("SHA-256", StringComparison.OrdinalIgnoreCase)),
        "Guest profile configuration is invalid.")
    .ValidateOnStart();
builder.Services.PostConfigure<StorageOptions>(options =>
{
    options.ServiceUrl = builder.Configuration["AWS_ENDPOINT_URL"] ?? options.ServiceUrl;
    options.BucketName = builder.Configuration["AWS_S3_BUCKET_NAME"] ?? options.BucketName;
    options.AccessKey = builder.Configuration["AWS_ACCESS_KEY_ID"] ?? options.AccessKey;
    options.SecretKey = builder.Configuration["AWS_SECRET_ACCESS_KEY"] ?? options.SecretKey;
    options.Region = builder.Configuration["AWS_DEFAULT_REGION"] ?? options.Region;

    var urlStyle = builder.Configuration["AWS_S3_URL_STYLE"];
    if (!string.IsNullOrWhiteSpace(urlStyle))
        options.ForcePathStyle = urlStyle.Equals("path", StringComparison.OrdinalIgnoreCase);
    else if (!string.IsNullOrWhiteSpace(builder.Configuration["AWS_ENDPOINT_URL"]))
        options.ForcePathStyle = false;
});
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddAuthentication(DevelopmentAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(
            DevelopmentAuthenticationHandler.SchemeName,
            _ => { });
}
else
{
    var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
        ?? throw new InvalidOperationException("JWT configuration is required.");
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwt.Issuer,
                ValidateAudience = true,
                ValidAudience = jwt.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1),
                NameClaimType = "sub",
                RoleClaimType = ClaimTypes.Role
            };
        });
}
builder.Services.AddAuthorization();

builder.Services.AddSingleton<IAmazonS3>(services =>
{
    var storage = services.GetRequiredService<IOptions<StorageOptions>>().Value;
    return new AmazonS3Client(storage.AccessKey, storage.SecretKey, new AmazonS3Config
    {
        ServiceURL = storage.ServiceUrl,
        ForcePathStyle = storage.ForcePathStyle,
        AuthenticationRegion = storage.Region
    });
});
builder.Services.AddSingleton(TimeProvider.System); builder.Services.AddMemoryCache(); builder.Services.AddScoped<ILanguageResolver, LanguageResolver>(); builder.Services.AddScoped<IStorageUrlService, StorageUrlService>(); builder.Services.AddScoped<IMealQueryService, MealQueryService>(); builder.Services.AddScoped<IGuestHomeService, GuestHomeService>(); builder.Services.AddScoped<IMealSelectionService, MealSelectionService>(); builder.Services.AddScoped<IAdminMealService, AdminMealService>(); builder.Services.AddScoped<ITemplateMenuReader>(services => (AdminMealService)services.GetRequiredService<IAdminMealService>()); builder.Services.AddScoped<IOperationalCalendarService, DefaultOperationalCalendarService>(); builder.Services.AddScoped<IDeliverySchedulingService, DeliverySchedulingService>(); builder.Services.AddScoped<IAuthService, AuthService>(); builder.Services.AddScoped<ICustomerProfileService, CustomerProfileService>(); builder.Services.AddSingleton(services => services.GetRequiredService<IOptions<CustomerNutritionOptions>>().Value); builder.Services.AddSingleton<ICustomerNutritionCalculator, CustomerNutritionCalculator>(); builder.Services.AddSingleton<IGuestOnboardingProgressResolver, GuestOnboardingProgressResolver>(); builder.Services.AddSingleton(services => services.GetRequiredService<IOptions<GuestProfileOptions>>().Value); builder.Services.AddSingleton<IGuestTokenGenerator, GuestTokenGenerator>(); builder.Services.AddSingleton<IGuestTokenHasher, GuestTokenHasher>(); builder.Services.AddScoped<IGuestTokenResolver, GuestTokenResolver>(); builder.Services.AddScoped<IGuestProfileService, GuestProfileService>(); builder.Services.AddScoped<IGuestPlanRecommendationService, GuestPlanRecommendationService>(); builder.Services.AddScoped<IGuestProfileCleanupService, GuestProfileCleanupService>(); builder.Services.AddHostedService<GuestProfileCleanupWorker>();
builder.Services.AddValidatorsFromAssemblyContaining<MealSelectionRequestValidator>(); builder.Services.AddFluentValidationAutoValidation();

builder.Services.AddControllers().AddJsonOptions(o => { o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull; o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(new UpperCaseJsonNamingPolicy())); });
builder.Services.AddApiVersioning(o => { o.DefaultApiVersion = new(1); o.AssumeDefaultVersionWhenUnspecified = true; o.ReportApiVersions = true; o.ApiVersionReader = new UrlSegmentApiVersionReader(); }).AddMvc().AddApiExplorer(o => { o.GroupNameFormat = "'v'VVV"; o.SubstituteApiVersionInUrl = true; });
builder.Services.AddEndpointsApiExplorer(); builder.Services.AddSwaggerGen(o => { o.SwaggerDoc("v1", new() { Title = "Diet Time Meal API", Version = "v1", Description = "Localized meal catalogue, plan selection, and administration API." }); o.AddSecurityDefinition("Bearer", new() { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT", Description = "JWT access token" }); o.OperationFilter<AuthorizeOperationFilter>(); var xml = Path.Combine(AppContext.BaseDirectory, "DietTime.Meal.Api.xml"); if (File.Exists(xml)) o.IncludeXmlComments(xml); });
builder.Services.AddProblemDetails(); builder.Services.AddHealthChecks().AddDbContextCheck<DietTimeDbContext>("postgresql");
var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? []; builder.Services.AddCors(o => o.AddPolicy("Flutter", p => { if (origins.Length > 0) p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials(); }));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("guest-session", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
    o.AddPolicy("guest-profile", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new() { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();
app.UseMiddleware<CorrelationIdMiddleware>(); app.UseSerilogRequestLogging(); app.UseMiddleware<ExceptionMiddleware>(); app.UseMiddleware<SecurityHeadersMiddleware>(); app.UseRateLimiter();
if (!app.Environment.IsProduction()) { app.UseSwagger(); app.UseSwaggerUI(o => o.SwaggerEndpoint("/swagger/v1/swagger.json", "Diet Time Meal API v1")); }
app.UseCors("Flutter"); if (!app.Environment.IsDevelopment()) app.UseHttpsRedirection(); app.UseAuthentication(); app.UseAuthorization(); app.MapControllers(); app.MapHealthChecks("/health");
app.Run();

public partial class Program { }

public sealed class UpperCaseJsonNamingPolicy : JsonNamingPolicy
{
    public override string ConvertName(string name) => name.ToUpperInvariant();
}
