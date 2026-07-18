using System.Security.Claims;
using CaseLedger.Api.Authentication;
using CaseLedger.Api.Contracts;
using CaseLedger.Api.Data;
using CaseLedger.Api.Domain;
using CaseLedger.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CaseLedger.Api.Endpoints;

public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapCaseLedgerApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true));
        var auth = api.MapGroup("/auth")
            .WithTags("Authentication");

        auth.MapGet("/antiforgery", GetAntiforgeryToken)
            .AllowAnonymous()
            .WithName("GetAntiforgeryToken")
            .WithSummary("Issue a request token for browser state-changing requests")
            .Produces<AntiforgeryTokenResponse>(StatusCodes.Status200OK);
        auth.MapGet("/capabilities", GetAuthCapabilities)
            .AllowAnonymous()
            .WithName("GetAuthCapabilities")
            .WithSummary("Read the enabled interactive sign-in methods")
            .Produces<AuthCapabilitiesResponse>(StatusCodes.Status200OK);
        auth.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting("login")
            .WithName("Login")
            .WithSummary("Create an authenticated session")
            .Produces<UserResponse>()
            .Produces<HttpValidationProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status429TooManyRequests);
        auth.MapGet("/entra/login", BeginEntraLogin)
            .AllowAnonymous()
            .RequireRateLimiting("login")
            .WithName("BeginEntraLogin")
            .WithSummary("Begin an optional Microsoft Entra sign-in")
            .Produces(StatusCodes.Status302Found)
            .Produces<HttpValidationProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status429TooManyRequests);
        auth.MapGet("/me", MeAsync)
            .RequireAuthorization()
            .RequireRateLimiting("authenticated")
            .WithName("GetCurrentUser")
            .WithSummary("Get the authenticated user")
            .Produces<UserResponse>()
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status429TooManyRequests);
        auth.MapPost("/logout", LogoutAsync)
            .RequireAuthorization()
            .RequireRateLimiting("authenticated")
            .WithName("Logout")
            .WithSummary("End the authenticated session")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status429TooManyRequests);

        api.MapGet("/users", GetUsersAsync)
            .RequireAuthorization()
            .RequireRateLimiting("authenticated")
            .WithTags("Users")
            .WithName("ListUsers")
            .WithSummary("List users available for assignment")
            .Produces<UserResponse[]>()
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status429TooManyRequests);
        api.MapAdminOperationsEndpoints();
        api.MapCaseEndpoints();

        return endpoints;
    }

    internal static async Task<User?> GetCurrentUserAsync(
        ClaimsPrincipal principal,
        CaseLedgerDbContext db,
        CancellationToken cancellationToken)
    {
        var idValue = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(idValue, out var id)
            ? await db.Users.SingleOrDefaultAsync(
                item => item.Id == id && item.IsActive,
                cancellationToken)
            : null;
    }

    private static IResult GetAuthCapabilities(
        IOptions<CaseLedgerAuthenticationOptions> options) =>
        Results.Ok(new AuthCapabilitiesResponse(
            options.Value.DemoLoginEnabled,
            options.Value.Entra.Enabled,
            options.Value.ShowDemoCredentials));

    private static IResult GetAntiforgeryToken(
        HttpContext context,
        IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        return Results.Ok(new AntiforgeryTokenResponse(
            tokens.RequestToken ?? throw new InvalidOperationException(
                "The antiforgery service did not issue a request token.")));
    }

    private static IResult BeginEntraLogin(
        [FromQuery] string? returnUrl,
        IOptions<CaseLedgerAuthenticationOptions> options)
    {
        if (!options.Value.Entra.Enabled)
        {
            return Results.NotFound();
        }

        if (!TryGetLocalReturnUrl(returnUrl, out var redirectUri))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["returnUrl"] = ["returnUrl must be a local application path."]
            });
        }

        return Results.Challenge(
            new AuthenticationProperties { RedirectUri = redirectUri },
            [AuthenticationSchemes.Entra]);
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        HttpContext context,
        CaseLedgerDbContext db,
        PasswordService passwords,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            errors["email"] = ["Email is required."];
        }

        if (string.IsNullOrEmpty(request.Password))
        {
            errors["password"] = ["Password is required."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var login = request.Email!.Trim();
        var normalizedEmail = login.ToUpperInvariant() switch
        {
            "ADMIN" => "ADMIN@CASELEDGER.DEV",
            "ANALYST" => "ANALYST@CASELEDGER.DEV",
            var value => value
        };

        var user = await db.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item =>
                    item.NormalizedEmail == normalizedEmail &&
                    item.IsActive &&
                    item.LocalLoginEnabled,
                cancellationToken);

        if (user is null || !passwords.VerifyPassword(request.Password!, user.PasswordHash))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Invalid credentials",
                detail: "The email or password is incorrect.");
        }

        var principal = SessionPrincipalFactory.Create(
            user,
            SessionPrincipalFactory.LocalAuthenticationSource);

        await context.SignInAsync(
            AuthenticationSchemes.Session,
            principal,
            new AuthenticationProperties
            {
                AllowRefresh = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8),
                IsPersistent = false
            });

        return Results.Ok(ToResponse(user));
    }

    private static async Task<IResult> MeAsync(
        ClaimsPrincipal principal,
        CaseLedgerDbContext db,
        CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(principal, db, cancellationToken);
        return user is null
            ? Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Authentication required")
            : Results.Ok(ToResponse(user));
    }

    private static async Task<IResult> LogoutAsync(HttpContext context, ClaimsPrincipal principal)
    {
        _ = principal;
        await context.SignOutAsync(AuthenticationSchemes.Session);
        return Results.NoContent();
    }

    private static async Task<IResult> GetUsersAsync(
        CaseLedgerDbContext db,
        CancellationToken cancellationToken)
    {
        var users = await db.Users
            .AsNoTracking()
            .Where(item => item.IsActive)
            .OrderBy(item => item.Name)
            .ToListAsync(cancellationToken);

        return Results.Ok(users.Select(ToResponse).ToArray());
    }

    private static UserResponse ToResponse(User user) =>
        new(user.Id, user.Name, user.Email, user.Role);

    private static bool TryGetLocalReturnUrl(
        string? value,
        out string redirectUri)
    {
        redirectUri = "/";
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (value[0] != '/' ||
            value.StartsWith("//", StringComparison.Ordinal) ||
            value.StartsWith("/\\", StringComparison.Ordinal) ||
            value.Contains('\\') ||
            value.Any(char.IsControl) ||
            Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            return false;
        }

        redirectUri = value;
        return true;
    }
}
