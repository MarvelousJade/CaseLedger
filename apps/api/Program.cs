using System.Text.Json.Serialization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using CaseLedger.Api.Data;
using CaseLedger.Api.Endpoints;
using CaseLedger.Api.GraphQL;
using CaseLedger.Api.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);
builder.AddCaseLedgerObservability();

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
    };
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "CaseLedger API",
        Version = "v1",
        Description = "Authenticated case-management and audit-integrity REST API. " +
                      "Call POST /api/auth/login first to establish the HTTP-only session cookie."
    });
});

builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
{
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var databaseProvider = builder.Configuration["Database:Provider"] ?? "Sqlite";
var connectionString = builder.Configuration.GetConnectionString("CaseLedger");
if (builder.Environment.IsProduction() &&
    databaseProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException(
        "Production requires Database:Provider=PostgreSQL so data is not stored on ephemeral disk.");
}

builder.Services.AddDbContext<CaseLedgerDbContext>(options =>
{
    if (databaseProvider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase))
    {
        options.UseNpgsql(connectionString ?? throw new InvalidOperationException(
            "ConnectionStrings:CaseLedger is required when Database:Provider is PostgreSQL."));
    }
    else if (databaseProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
    {
        options.UseSqlite(connectionString ?? "Data Source=caseledger.db");
    }
    else
    {
        throw new InvalidOperationException(
            $"Unsupported Database:Provider '{databaseProvider}'. Use Sqlite or PostgreSQL.");
    }
});

builder.Services.AddSingleton<PasswordService>();
builder.Services.AddScoped<AuditChainService>();
builder.Services.AddScoped<DatabaseSeeder>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
    options.AddPolicy("authenticated", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ??
        context.Connection.RemoteIpAddress?.ToString() ??
        "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
});

var requireSecureCookies = builder.Configuration.GetValue(
    "Security:RequireSecureCookies",
    builder.Environment.IsProduction());
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "CaseLedger.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.IsEssential = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = requireSecureCookies
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context => WriteAuthProblemAsync(
            context.HttpContext,
            StatusCodes.Status401Unauthorized,
            "Authentication required");
        options.Events.OnRedirectToAccessDenied = context => WriteAuthProblemAsync(
            context.HttpContext,
            StatusCodes.Status403Forbidden,
            "Access denied");
    });
builder.Services.AddAuthorization();

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ??
    ["http://localhost:5173", "http://127.0.0.1:5173"];
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

builder.Services
    .AddGraphQLServer()
    .AddQueryType<DashboardQuery>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.DocumentTitle = "CaseLedger API documentation";
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "CaseLedger API v1");
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
    .AllowAnonymous()
    .WithName("Health")
    .WithTags("System")
    .WithSummary("Check API process health")
    .Produces(StatusCodes.Status200OK);
app.MapCaseLedgerApi();
app.MapGraphQL("/graphql")
    .RequireAuthorization()
    .RequireRateLimiting("authenticated")
    .ExcludeFromDescription();
app.MapFallbackToFile("index.html");

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
    if (db.Database.IsNpgsql())
    {
        await db.Database.MigrateAsync();
    }
    else
    {
        await db.Database.EnsureCreatedAsync();
    }
    var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
    await seeder.SeedAsync();
}

await app.RunAsync();

static Task WriteAuthProblemAsync(
    HttpContext context,
    int statusCode,
    string title)
{
    return Results.Problem(
        statusCode: statusCode,
        title: title,
        type: $"https://httpstatuses.com/{statusCode}")
        .ExecuteAsync(context);
}

public partial class Program;
