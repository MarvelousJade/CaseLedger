namespace CaseLedger.Api.Authentication;

public sealed class CaseLedgerAuthenticationOptions
{
    public const string SectionName = "Authentication";

    public bool DemoLoginEnabled { get; init; }
    public bool ShowDemoCredentials { get; init; }
    public EntraAuthenticationOptions Entra { get; init; } = new();
}

public sealed class EntraAuthenticationOptions
{
    public bool Enabled { get; init; }
    public string? TenantId { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string CallbackPath { get; init; } = "/signin-oidc";
    public bool AutoProvisionAnalyst { get; init; }
    public string? BootstrapAdministratorObjectId { get; init; }
}

public static class AuthenticationRuntimeOptions
{
    public static void Validate(CaseLedgerAuthenticationOptions options)
    {
        if (options.ShowDemoCredentials && !options.DemoLoginEnabled)
        {
            throw new InvalidOperationException(
                "Authentication:ShowDemoCredentials requires demo login to be enabled.");
        }

        if (!options.Entra.Enabled)
        {
            return;
        }

        if (!Guid.TryParse(options.Entra.TenantId, out var tenantId) ||
            tenantId == Guid.Empty ||
            !Guid.TryParse(options.Entra.ClientId, out var clientId) ||
            clientId == Guid.Empty ||
            string.IsNullOrWhiteSpace(options.Entra.ClientSecret) ||
            !IsSafeCallbackPath(options.Entra.CallbackPath) ||
            (!string.IsNullOrWhiteSpace(
                 options.Entra.BootstrapAdministratorObjectId) &&
             (!Guid.TryParse(
                  options.Entra.BootstrapAdministratorObjectId,
                  out var bootstrapObjectId) ||
              bootstrapObjectId == Guid.Empty)))
        {
            throw new InvalidOperationException(
                "Authentication:Entra requires a tenant ID, client ID, client secret, " +
                "a local callback path, and an optional bootstrap object ID when enabled.");
        }
    }

    private static bool IsSafeCallbackPath(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value[0] == '/' &&
        !value.StartsWith("//", StringComparison.Ordinal) &&
        !value.StartsWith("/\\", StringComparison.Ordinal) &&
        value.IndexOfAny(['?', '#', '\r', '\n']) < 0;
}
