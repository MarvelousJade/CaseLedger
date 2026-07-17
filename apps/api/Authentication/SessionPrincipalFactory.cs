using System.Security.Claims;
using CaseLedger.Api.Domain;

namespace CaseLedger.Api.Authentication;

public static class SessionPrincipalFactory
{
    public const string AuthenticationSourceClaim =
        "caseledger:authentication_source";
    public const string LocalAuthenticationSource = "local";
    public const string EntraAuthenticationSource = "entra";

    public static ClaimsPrincipal Create(User user, string authenticationSource)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString("D")),
            new Claim(ClaimTypes.Name, user.Name),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim(AuthenticationSourceClaim, authenticationSource)
        };
        var identity = new ClaimsIdentity(
            claims,
            AuthenticationSchemes.Session,
            ClaimTypes.Name,
            ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }
}
