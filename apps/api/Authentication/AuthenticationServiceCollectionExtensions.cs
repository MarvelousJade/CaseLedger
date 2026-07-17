using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace CaseLedger.Api.Authentication;

public static class AuthenticationServiceCollectionExtensions
{
    public static IServiceCollection AddCaseLedgerExternalAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(
            CaseLedgerAuthenticationOptions.SectionName);
        services.Configure<CaseLedgerAuthenticationOptions>(section);
        services.AddScoped<ExternalIdentityService>();
        services.AddScoped<EntraSignInService>();

        var settings = section.Get<CaseLedgerAuthenticationOptions>() ??
            new CaseLedgerAuthenticationOptions();
        AuthenticationRuntimeOptions.Validate(settings);
        if (!settings.Entra.Enabled)
        {
            return services;
        }

        var tenantId = Guid.Parse(settings.Entra.TenantId!);
        services.AddAuthentication()
            .AddOpenIdConnect(AuthenticationSchemes.Entra, options =>
            {
                options.SignInScheme = AuthenticationSchemes.Session;
                options.Authority =
                    $"https://login.microsoftonline.com/{tenantId:D}/v2.0";
                options.ClientId = settings.Entra.ClientId!;
                options.ClientSecret = settings.Entra.ClientSecret!;
                options.CallbackPath = settings.Entra.CallbackPath;
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.UsePkce = true;
                options.RequireHttpsMetadata = true;
                options.SaveTokens = false;
                options.GetClaimsFromUserInfoEndpoint = false;
                options.MapInboundClaims = false;
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.TokenValidationParameters.ValidateIssuer = true;
                options.TokenValidationParameters.ValidIssuer =
                    $"https://login.microsoftonline.com/{tenantId:D}/v2.0";
                options.TokenValidationParameters.NameClaimType = "name";
                options.TokenValidationParameters.RoleClaimType = "roles";
                options.Events.OnTokenValidated = async context =>
                {
                    var signIn = context.HttpContext.RequestServices
                        .GetRequiredService<EntraSignInService>();
                    var principal = await signIn.CreateSessionPrincipalAsync(
                        context.Principal!,
                        context.HttpContext.RequestAborted);
                    if (principal is null)
                    {
                        context.Fail("The external identity is not authorized.");
                        return;
                    }

                    context.Principal = principal;
                };
                options.Events.OnRemoteFailure = context =>
                {
                    context.HandleResponse();
                    context.Response.Redirect(
                        "/?authError=external-sign-in-failed");
                    return Task.CompletedTask;
                };
            });

        return services;
    }
}
