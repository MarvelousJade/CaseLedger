using System.Security.Claims;
using CaseLedger.Api.Contracts;
using CaseLedger.Api.Data;
using CaseLedger.Api.Domain;
using CaseLedger.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CaseLedger.Api.Endpoints;

public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapCaseLedgerApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");
        var auth = api.MapGroup("/auth")
            .WithTags("Authentication");

        auth.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting("login")
            .WithName("Login")
            .WithSummary("Create an authenticated session")
            .Produces<UserResponse>()
            .Produces<HttpValidationProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
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
            ? await db.Users.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            : null;
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
            .SingleOrDefaultAsync(item => item.NormalizedEmail == normalizedEmail, cancellationToken);

        if (user is null || !passwords.VerifyPassword(request.Password!, user.PasswordHash))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Invalid credentials",
                detail: "The email or password is incorrect.");
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString("D")),
            new Claim(ClaimTypes.Name, user.Name),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role)
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
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
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.NoContent();
    }

    private static async Task<IResult> GetUsersAsync(
        CaseLedgerDbContext db,
        CancellationToken cancellationToken)
    {
        var users = await db.Users
            .AsNoTracking()
            .OrderBy(item => item.Name)
            .ToListAsync(cancellationToken);

        return Results.Ok(users.Select(ToResponse).ToArray());
    }

    private static UserResponse ToResponse(User user) =>
        new(user.Id, user.Name, user.Email, user.Role);
}
