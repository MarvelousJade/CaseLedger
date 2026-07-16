using System.Text.Json.Serialization;
using CaseLedger.Api.Data;
using CaseLedger.Api.Endpoints;
using CaseLedger.Api.GraphQL;
using CaseLedger.Api.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
    };
});

builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
{
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var databaseProvider = builder.Configuration["Database:Provider"] ?? "Sqlite";
var connectionString = builder.Configuration.GetConnectionString("CaseLedger");
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

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "CaseLedger.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.IsEssential = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
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
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();
app.MapCaseLedgerApi();
app.MapGraphQL("/graphql").RequireAuthorization();
app.MapFallbackToFile("index.html");

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
    await db.Database.EnsureCreatedAsync();
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
