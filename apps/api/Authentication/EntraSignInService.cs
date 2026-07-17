using System.Security.Claims;
using Microsoft.Extensions.Options;

namespace CaseLedger.Api.Authentication;

public sealed class EntraSignInService(
    ExternalIdentityService externalIdentities,
    IOptions<CaseLedgerAuthenticationOptions> options)
{
    public async Task<ClaimsPrincipal?> CreateSessionPrincipalAsync(
        ClaimsPrincipal externalPrincipal,
        CancellationToken cancellationToken = default)
    {
        if (!options.Value.Entra.Enabled ||
            !Guid.TryParse(options.Value.Entra.TenantId, out var configuredTenantId) ||
            !Guid.TryParse(externalPrincipal.FindFirstValue("tid"), out var tenantId) ||
            !Guid.TryParse(externalPrincipal.FindFirstValue("oid"), out var objectId) ||
            tenantId != configuredTenantId)
        {
            return null;
        }

        var user = await externalIdentities.ResolveAsync(
            tenantId,
            objectId,
            cancellationToken);
        return user is null
            ? null
            : SessionPrincipalFactory.Create(
                user,
                SessionPrincipalFactory.EntraAuthenticationSource);
    }
}
