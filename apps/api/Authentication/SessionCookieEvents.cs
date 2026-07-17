using System.Security.Claims;
using CaseLedger.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace CaseLedger.Api.Authentication;

public sealed class SessionCookieEvents(CaseLedgerDbContext db)
    : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(
        CookieValidatePrincipalContext context)
    {
        var idValue = context.Principal?
            .FindFirstValue(ClaimTypes.NameIdentifier);
        var authenticationSource = context.Principal?
            .FindFirstValue(SessionPrincipalFactory.AuthenticationSourceClaim);
        var user = Guid.TryParse(idValue, out var userId)
            ? await db.Users
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.Id == userId,
                    context.HttpContext.RequestAborted)
            : null;
        var localSessionAllowed =
            authenticationSource != SessionPrincipalFactory.LocalAuthenticationSource ||
            user?.LocalLoginEnabled == true;
        if (user is null || !user.IsActive || !localSessionAllowed)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(AuthenticationSchemes.Session);
            return;
        }

        var currentName = context.Principal?.FindFirstValue(ClaimTypes.Name);
        var currentEmail = context.Principal?.FindFirstValue(ClaimTypes.Email);
        var currentRole = context.Principal?.FindFirstValue(ClaimTypes.Role);
        if (currentName != user.Name ||
            currentEmail != user.Email ||
            currentRole != user.Role)
        {
            context.ReplacePrincipal(SessionPrincipalFactory.Create(
                user,
                authenticationSource ??
                SessionPrincipalFactory.LocalAuthenticationSource));
            context.ShouldRenew = true;
        }
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context) =>
        WriteProblemAsync(
            context.HttpContext,
            StatusCodes.Status401Unauthorized,
            "Authentication required");

    public override Task RedirectToAccessDenied(
        RedirectContext<CookieAuthenticationOptions> context) =>
        WriteProblemAsync(
            context.HttpContext,
            StatusCodes.Status403Forbidden,
            "Access denied");

    private static Task WriteProblemAsync(
        HttpContext context,
        int statusCode,
        string title) =>
        Results.Problem(
                statusCode: statusCode,
                title: title,
                type: $"https://httpstatuses.com/{statusCode}")
            .ExecuteAsync(context);
}
