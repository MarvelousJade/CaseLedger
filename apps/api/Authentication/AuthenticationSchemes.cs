using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace CaseLedger.Api.Authentication;

public static class AuthenticationSchemes
{
    public const string Forwarding = "CaseLedger.Authentication";
    public const string Session = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string Bearer = JwtBearerDefaults.AuthenticationScheme;
    public const string Entra = "CaseLedger.Entra";
}
