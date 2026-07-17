using System.Security.Cryptography;
using CaseLedger.Api.Data;
using CaseLedger.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CaseLedger.Api.Authentication;

public sealed class ExternalIdentityService(
    CaseLedgerDbContext db,
    IOptions<CaseLedgerAuthenticationOptions> options,
    TimeProvider clock)
{
    public const string MicrosoftEntraProvider = "entra";
    private const string ExternalOnlyPasswordHash = "external-login-only";

    public async Task<User?> ResolveAsync(
        Guid tenantId,
        Guid objectId,
        CancellationToken cancellationToken = default)
    {
        var configuredTenantId = ConfiguredTenantId();
        if (tenantId == Guid.Empty ||
            objectId == Guid.Empty ||
            configuredTenantId is null ||
            tenantId != configuredTenantId.Value)
        {
            return null;
        }

        var existing = await FindActiveUserAsync(
            tenantId,
            objectId,
            cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        if (!options.Value.Entra.Enabled ||
            !options.Value.Entra.AutoProvisionAnalyst)
        {
            return null;
        }

        var identifier = PseudonymousIdentifier(tenantId, objectId);
        var user = new User
        {
            Id = Guid.NewGuid(),
            Name = $"Entra Analyst {identifier[..8].ToUpperInvariant()}",
            Email = $"entra-{identifier}@identity.caseledger.invalid",
            NormalizedEmail =
                $"ENTRA-{identifier.ToUpperInvariant()}@IDENTITY.CASELEDGER.INVALID",
            Role = "Analyst",
            PasswordHash = ExternalOnlyPasswordHash,
            IsActive = true,
            LocalLoginEnabled = false
        };
        var externalIdentity = new ExternalIdentity
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            User = user,
            Provider = MicrosoftEntraProvider,
            TenantId = tenantId,
            ObjectId = objectId,
            CreatedAt = NormalizeUtcToMicroseconds(clock.GetUtcNow().UtcDateTime)
        };

        db.Users.Add(user);
        db.ExternalIdentities.Add(externalIdentity);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return user;
        }
        catch (DbUpdateException)
        {
            // A concurrent first sign-in may have created the same unique mapping.
            db.ChangeTracker.Clear();
            var concurrentlyCreated = await FindActiveUserAsync(
                tenantId,
                objectId,
                cancellationToken);
            if (concurrentlyCreated is not null)
            {
                return concurrentlyCreated;
            }

            throw;
        }
    }

    private Guid? ConfiguredTenantId() =>
        Guid.TryParse(options.Value.Entra.TenantId, out var tenantId)
            ? tenantId
            : null;

    private Task<User?> FindActiveUserAsync(
        Guid tenantId,
        Guid objectId,
        CancellationToken cancellationToken) =>
        db.ExternalIdentities
            .AsNoTracking()
            .Where(item =>
                item.Provider == MicrosoftEntraProvider &&
                item.TenantId == tenantId &&
                item.ObjectId == objectId &&
                item.User.IsActive)
            .Select(item => item.User)
            .SingleOrDefaultAsync(cancellationToken);

    private static string PseudonymousIdentifier(Guid tenantId, Guid objectId)
    {
        Span<byte> input = stackalloc byte[32];
        tenantId.TryWriteBytes(input[..16]);
        objectId.TryWriteBytes(input[16..]);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    private static DateTime NormalizeUtcToMicroseconds(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
        return new DateTime(
            utc.Ticks - utc.Ticks % TimeSpan.TicksPerMicrosecond,
            DateTimeKind.Utc);
    }
}
