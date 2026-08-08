using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
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
using Npgsql;
using Serilog;
using JwtOptions = DietTime.Persistence.JwtOptions;

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

builder.Services.AddSingleton<GuestHomeCacheVersion>();
builder.Services.AddSingleton<GuestHomeCacheInvalidationInterceptor>();
builder.Services.AddDbContext<DietTimeDbContext>((services, options) =>
{
    var connection = DatabaseConnectionStringResolver.Resolve(
        services.GetRequiredService<IConfiguration>());

    options
        .UseNpgsql(connection)
        .UseSnakeCaseNamingConvention()
        .AddInterceptors(services.GetRequiredService<GuestHomeCacheInvalidationInterceptor>());
});
builder.Services.AddIdentityCore<ApplicationUser>(o => { o.Password.RequiredLength = 10; o.Password.RequireDigit = true; o.Password.RequireUppercase = true; o.Lockout.MaxFailedAccessAttempts = 5; o.SignIn.RequireConfirmedEmail = false; })
    .AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<DietTimeDbContext>().AddDefaultTokenProviders();

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
builder.Services.Configure<PhoneOtpOptions>(builder.Configuration.GetSection(PhoneOtpOptions.SectionName));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.Configure<DeliveryScheduleOptions>(builder.Configuration.GetSection(DeliveryScheduleOptions.SectionName));
builder.Services.Configure<CustomerNutritionOptions>(builder.Configuration.GetSection(CustomerNutritionOptions.SectionName));
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.SectionName));
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

// Configure JWT Authentication
var jwtOptions = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
var key = Encoding.ASCII.GetBytes(jwtOptions.Key);

builder.Services.AddAuthentication(options =>
{
    if (builder.Environment.IsDevelopment())
    {
        // Real UI sessions send JWTs. Keep the development identity only as a
        // fallback for local requests that do not include a bearer token.
        options.DefaultScheme = "DevelopmentOrJwt";
        options.DefaultChallengeScheme = "DevelopmentOrJwt";
    }
    else
    {
        // Use JWT in production
        options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    }
})
.AddPolicyScheme("DevelopmentOrJwt", "Development JWT selector", options =>
{
    options.ForwardDefaultSelector = context =>
        context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? JwtBearerDefaults.AuthenticationScheme
            : DevelopmentAuthenticationHandler.SchemeName;
})
.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(key),
        ValidateIssuer = true,
        ValidIssuer = jwtOptions.Issuer,
        ValidateAudience = true,
        ValidAudience = jwtOptions.Audience,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(10)
    };
})
.AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(
    DevelopmentAuthenticationHandler.SchemeName,
    _ => { });

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
 builder.Services.AddMemoryCache(); builder.Services.AddScoped<ILanguageResolver, LanguageResolver>(); builder.Services.AddScoped<IStorageUrlService, StorageUrlService>(); builder.Services.AddScoped<IMealQueryService, MealQueryService>(); builder.Services.AddScoped<IGuestHomeService, GuestHomeService>(); builder.Services.AddScoped<IMealSelectionService, MealSelectionService>(); builder.Services.AddScoped<IAdminMealService, AdminMealService>(); builder.Services.AddScoped<ITemplateMenuReader>(services => (AdminMealService)services.GetRequiredService<IAdminMealService>()); builder.Services.AddScoped<IOperationalCalendarService, DefaultOperationalCalendarService>(); builder.Services.AddScoped<IDeliverySchedulingService, DeliverySchedulingService>(); builder.Services.AddScoped<IPhoneOtpVerifier, TestPhoneOtpVerifier>(); builder.Services.AddScoped<IAuthService, AuthService>(); builder.Services.AddScoped<IUserProfileService, UserProfileService>(); builder.Services.AddScoped<ICustomerService, CustomerService>(); builder.Services.AddScoped<IApplicationRoleService, ApplicationRoleService>(); builder.Services.AddScoped<IMenuService, MenuService>(); builder.Services.AddScoped<IRoleMenuMappingService, RoleMenuMappingService>(); builder.Services.AddScoped<IUserMenuService, UserMenuService>(); builder.Services.AddScoped<IAccessControlService, AccessControlService>(); builder.Services.AddScoped<IPasswordService, PasswordService>(); builder.Services.AddScoped<IEmailService, EmailService>()builder.Services.AddSingleton(TimeProvider.System); builder.Services.AddMemoryCache(); builder.Services.AddScoped<ILanguageResolver, LanguageResolver>(); builder.Services.AddScoped<IStorageUrlService, StorageUrlService>(); builder.Services.AddScoped<IMealQueryService, MealQueryService>(); builder.Services.AddScoped<IGuestHomeService, GuestHomeService>(); builder.Services.AddScoped<IMealSelectionService, MealSelectionService>(); builder.Services.AddScoped<IAdminMealService, AdminMealService>(); builder.Services.AddScoped<ITemplateMenuReader>(services => (AdminMealService)services.GetRequiredService<IAdminMealService>()); builder.Services.AddScoped<IOperationalCalendarService, DefaultOperationalCalendarService>(); builder.Services.AddScoped<IDeliverySchedulingService, DeliverySchedulingService>(); builder.Services.AddScoped<IAuthService, AuthService>(); builder.Services.AddScoped<ICustomerProfileService, CustomerProfileService>(); builder.Services.AddSingleton(services => services.GetRequiredService<IOptions<CustomerNutritionOptions>>().Value); builder.Services.AddSingleton<ICustomerNutritionCalculator, CustomerNutritionCalculator>();
builder.Services.AddSingleton(TimeProvider.System);;
builder.Services.AddValidatorsFromAssemblyContaining<MealSelectionRequestValidator>(); builder.Services.AddFluentValidationAutoValidation();

builder.Services.AddControllers().AddJsonOptions(o => { o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull; o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(new UpperCaseJsonNamingPolicy())); });
builder.Services.AddApiVersioning(o => { o.DefaultApiVersion = new(1); o.AssumeDefaultVersionWhenUnspecified = true; o.ReportApiVersions = true; o.ApiVersionReader = new UrlSegmentApiVersionReader(); }).AddMvc().AddApiExplorer(o => { o.GroupNameFormat = "'v'VVV"; o.SubstituteApiVersionInUrl = true; });
builder.Services.AddEndpointsApiExplorer(); builder.Services.AddSwaggerGen(o => { o.SwaggerDoc("v1", new() { Title = "Diet Time Meal API", Version = "v1", Description = "Localized meal catalogue, plan selection, and administration API." }); o.AddSecurityDefinition("Bearer", new() { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT", Description = "JWT access token" }); o.OperationFilter<AuthorizeOperationFilter>(); var xml = Path.Combine(AppContext.BaseDirectory, "DietTime.Meal.Api.xml"); if (File.Exists(xml)) o.IncludeXmlComments(xml); });
builder.Services.AddProblemDetails(); builder.Services.AddHealthChecks().AddDbContextCheck<DietTimeDbContext>("postgresql");
var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? []; builder.Services.AddCors(o => o.AddPolicy("Flutter", p => { if (origins.Length > 0) p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials(); }));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new() { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddFixedWindowLimiter("phone-otp", limiter =>
    {
        limiter.PermitLimit = 10;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
});

var app = builder.Build();
app.UseMiddleware<CorrelationIdMiddleware>(); app.UseSerilogRequestLogging(); app.UseMiddleware<ExceptionMiddleware>(); app.UseMiddleware<SecurityHeadersMiddleware>(); app.UseRateLimiter();
if (!app.Environment.IsProduction()) { app.UseSwagger(); app.UseSwaggerUI(o => o.SwaggerEndpoint("/swagger/v1/swagger.json", "Diet Time Meal API v1")); }
app.UseCors("Flutter"); if (!app.Environment.IsDevelopment()) app.UseHttpsRedirection(); app.UseAuthentication(); app.UseAuthorization(); app.MapControllers(); app.MapHealthChecks("/health");
app.Run();

public partial class Program { }

public static class DatabaseConnectionStringResolver
{
    public static string Resolve(IConfiguration configuration)
    {
        var environmentCandidate = configuration["DTDBCONNECTION"];
        Exception? environmentError = null;
        if (!string.IsNullOrWhiteSpace(environmentCandidate))
        {
            try
            {
                return Normalize(environmentCandidate);
            }
            catch (InvalidOperationException exception)
            {
                environmentError = exception;
            }
        }

        var configuredCandidate = configuration.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrWhiteSpace(configuredCandidate))
            return Normalize(configuredCandidate);

        throw environmentError ?? new InvalidOperationException(
            "Set DTDBCONNECTION or ConnectionStrings:DefaultConnection to a PostgreSQL connection string.");
    }

    public static string Normalize(string candidate)
    {
        candidate = candidate.Trim();
        if (candidate.Length >= 2 &&
            ((candidate[0] == '"' && candidate[^1] == '"') ||
             (candidate[0] == '\'' && candidate[^1] == '\'')))
            candidate = candidate[1..^1].Trim();

        try
        {
            if (candidate.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
                return FromPostgresUri(candidate);

            return new NpgsqlConnectionStringBuilder(candidate).ConnectionString;
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException)
        {
            throw new InvalidOperationException(
                "DTDBCONNECTION is not a valid PostgreSQL connection string or postgres:// URI.",
                exception);
        }
    }

    private static string FromPostgresUri(string value)
    {
        var uri = new Uri(value);
        var credentials = uri.UserInfo.Split(':', 2);
        if (credentials.Length != 2 || string.IsNullOrWhiteSpace(uri.Host))
            throw new UriFormatException("PostgreSQL URI must include host, username, and password.");

        return new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(credentials[0]),
            Password = Uri.UnescapeDataString(credentials[1]),
            SslMode = SslMode.Require
        }.ConnectionString;
    }
}

public sealed class UpperCaseJsonNamingPolicy : JsonNamingPolicy
{
    public override string ConvertName(string name) => name.ToUpperInvariant();
}
