using Microsoft.AspNetCore.Authentication.Cookies;

namespace CaseLedger.Api.Authentication;

public static class AuthenticationSchemes
{
    public const string Session = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string Entra = "CaseLedger.Entra";
}
