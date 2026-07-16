using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Amazon.S3;
using Asp.Versioning;
using DietTime.Meal.Api.Authentication;
using DietTime.Meal.Api.Middleware;
using DietTime.Application;
using DietTime.Infrastructure;
using DietTime.Persistence;
using FluentValidation;
using FluentValidation.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}
builder.Host.UseSerilog((context, config) => config.ReadFrom.Configuration(context.Configuration).Enrich.FromLogContext().WriteTo.Console());

var connection = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.");
builder.Services.AddDbContext<DietTimeDbContext>(o => o.UseNpgsql(connection).UseSnakeCaseNamingConvention());
builder.Services.AddIdentityCore<ApplicationUser>(o => { o.Password.RequiredLength = 10; o.Password.RequireDigit = true; o.Password.RequireUppercase = true; o.Lockout.MaxFailedAccessAttempts = 5; o.SignIn.RequireConfirmedEmail = false; })
    .AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<DietTimeDbContext>().AddDefaultTokenProviders();

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.AddAuthentication(DevelopmentAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(
        DevelopmentAuthenticationHandler.SchemeName,
        _ => { });
builder.Services.AddAuthorization();

var storage = builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new();
builder.Services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(storage.AccessKey, storage.SecretKey, new AmazonS3Config { ServiceURL = storage.ServiceUrl, ForcePathStyle = true, AuthenticationRegion = storage.Region }));
builder.Services.AddSingleton(TimeProvider.System); builder.Services.AddMemoryCache(); builder.Services.AddScoped<ILanguageResolver, LanguageResolver>(); builder.Services.AddScoped<IStorageUrlService, StorageUrlService>(); builder.Services.AddScoped<IMealQueryService, MealQueryService>(); builder.Services.AddScoped<IMealSelectionService, MealSelectionService>(); builder.Services.AddScoped<IAdminMealService, AdminMealService>(); builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddValidatorsFromAssemblyContaining<MealSelectionRequestValidator>(); builder.Services.AddFluentValidationAutoValidation();

builder.Services.AddControllers().AddJsonOptions(o => { o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull; o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()); });
builder.Services.AddApiVersioning(o => { o.DefaultApiVersion = new(1); o.AssumeDefaultVersionWhenUnspecified = true; o.ReportApiVersions = true; o.ApiVersionReader = new UrlSegmentApiVersionReader(); }).AddMvc().AddApiExplorer(o => { o.GroupNameFormat = "'v'VVV"; o.SubstituteApiVersionInUrl = true; });
builder.Services.AddEndpointsApiExplorer(); builder.Services.AddSwaggerGen(o => { o.SwaggerDoc("v1", new() { Title = "Diet Time Meal API", Version = "v1", Description = "Localized meal catalogue, plan selection, and administration API." }); o.AddSecurityDefinition("Bearer", new() { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT", Description = "JWT access token" }); o.AddSecurityRequirement(new() { [new OpenApiSecurityScheme { Reference = new() { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = [] }); var xml = Path.Combine(AppContext.BaseDirectory, "DietTime.Meal.Api.xml"); if (File.Exists(xml)) o.IncludeXmlComments(xml); });
builder.Services.AddProblemDetails(); builder.Services.AddHealthChecks().AddDbContextCheck<DietTimeDbContext>("postgresql");
var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? []; builder.Services.AddCors(o => o.AddPolicy("Flutter", p => { if (origins.Length > 0) p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials(); }));
builder.Services.AddRateLimiter(o => { o.RejectionStatusCode = 429; o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new() { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })); });

var app = builder.Build();
if (app.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("DevelopmentSeed:Enabled")) { await using var scope = app.Services.CreateAsyncScope(); await DevelopmentSeeder.SeedAsync(scope.ServiceProvider.GetRequiredService<DietTimeDbContext>()); }
app.UseMiddleware<CorrelationIdMiddleware>(); app.UseMiddleware<ExceptionMiddleware>(); app.UseSerilogRequestLogging(); app.UseMiddleware<SecurityHeadersMiddleware>(); app.UseRateLimiter();
if (!app.Environment.IsProduction()) { app.UseSwagger(); app.UseSwaggerUI(o => o.SwaggerEndpoint("/swagger/v1/swagger.json", "Diet Time Meal API v1")); }
app.UseCors("Flutter"); if (!app.Environment.IsDevelopment()) app.UseHttpsRedirection(); app.UseAuthentication(); app.UseAuthorization(); app.MapControllers(); app.MapHealthChecks("/health");
app.Run();

public partial class Program { }
