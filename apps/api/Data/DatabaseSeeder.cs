using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CaseLedger.Api.Authentication;
using CaseLedger.Api.Domain;
using CaseLedger.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CaseLedger.Api.Data;

public sealed class DatabaseSeeder(
    CaseLedgerDbContext db,
    PasswordService passwords,
    AuditChainService auditChain,
    IConfiguration configuration,
    IHostEnvironment environment,
    IOptions<CaseLedgerAuthenticationOptions> authenticationOptions,
    TimeProvider clock)
{
    public static readonly Guid AdminId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    public static readonly Guid AnalystId = Guid.Parse("10000000-0000-0000-0000-000000000002");

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var demoLoginEnabled = authenticationOptions.Value.DemoLoginEnabled;
        var adminPassword = GetAdminPassword(demoLoginEnabled);
        var analystPassword = GetAnalystPassword(demoLoginEnabled);

        if (!await db.Users.AnyAsync(cancellationToken))
        {
            db.Users.AddRange(
                new User
                {
                    Id = AdminId,
                    Name = "Morgan Chen",
                    Email = "admin@caseledger.dev",
                    NormalizedEmail = "ADMIN@CASELEDGER.DEV",
                    Role = "Admin",
                    PasswordHash = passwords.HashPassword(
                        adminPassword ?? GenerateDisabledAccountPassword()),
                    LocalLoginEnabled = demoLoginEnabled
                },
                new User
                {
                    Id = AnalystId,
                    Name = "Avery Singh",
                    Email = "analyst@caseledger.dev",
                    NormalizedEmail = "ANALYST@CASELEDGER.DEV",
                    Role = "Analyst",
                    PasswordHash = passwords.HashPassword(
                        analystPassword ?? GenerateDisabledAccountPassword()),
                    LocalLoginEnabled = demoLoginEnabled
                });

            await db.SaveChangesAsync(cancellationToken);
        }

        await ReconcileDemoLoginPolicyAsync(
            demoLoginEnabled,
            adminPassword,
            analystPassword,
            cancellationToken);
        await EnsureEntraBootstrapAdministratorAsync(
            authenticationOptions.Value.Entra,
            cancellationToken);

        if (await db.Cases.AnyAsync(cancellationToken))
        {
            return;
        }

        var admin = await db.Users.SingleAsync(item => item.Id == AdminId, cancellationToken);
        var analyst = await db.Users.SingleAsync(item => item.Id == AnalystId, cancellationToken);

        var cases = new[]
        {
            NewCase(
                1,
                "CL-2026-001",
                "Investigate anomalous administrator sign-ins",
                "Multiple privileged sign-ins originated from an unrecognized network during the maintenance window.",
                CaseStatus.InProgress,
                CaseSeverity.Critical,
                "Security Operations",
                analyst,
                admin,
                Utc(2026, 7, 12, 13, 30),
                Utc(2026, 7, 17, 17, 0),
                ["identity", "priority"]),
            NewCase(
                2,
                "CL-2026-002",
                "Reconcile customer export mismatch",
                "A customer export contains fewer records than the corresponding dashboard summary.",
                CaseStatus.New,
                CaseSeverity.High,
                "Data Integrity",
                analyst,
                analyst,
                Utc(2026, 7, 14, 15, 10),
                Utc(2026, 7, 20, 17, 0),
                ["export", "customer"]),
            NewCase(
                3,
                "CL-2026-003",
                "Validate mobile evidence package",
                "Review the supplied device package and confirm each evidence digest against the collection manifest.",
                CaseStatus.Resolved,
                CaseSeverity.Medium,
                "Digital Evidence",
                admin,
                analyst,
                Utc(2026, 7, 8, 10, 0),
                null,
                ["mobile", "evidence"]),
            NewCase(
                4,
                "CL-2026-004",
                "Review access-control policy anomaly",
                "A policy comparison found an unexpected permission grant in the production support group.",
                CaseStatus.New,
                CaseSeverity.Critical,
                "Access Control",
                null,
                admin,
                Utc(2026, 7, 15, 9, 45),
                Utc(2026, 7, 18, 17, 0),
                ["permissions", "review"]),
            NewCase(
                5,
                "CL-2026-005",
                "Document vendor webhook interruption",
                "Capture the timeline and remediation notes for a short-lived vendor webhook interruption.",
                CaseStatus.Resolved,
                CaseSeverity.Low,
                "Service Reliability",
                analyst,
                analyst,
                Utc(2026, 7, 6, 14, 20),
                null,
                ["vendor", "webhook"])
        };

        db.Cases.AddRange(cases);

        var seedEvidence = new Evidence
        {
            Id = Guid.Parse("40000000-0000-0000-0000-000000000001"),
            CaseId = cases[0].Id,
            Case = cases[0],
            FileName = "signin-events.csv",
            SizeBytes = 18_432,
            MediaType = "text/csv",
            Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("caseledger-seed-evidence")))
                .ToLowerInvariant(),
            AddedById = admin.Id,
            AddedBy = admin,
            CreatedAt = Utc(2026, 7, 12, 13, 45)
        };
        db.Evidence.Add(seedEvidence);

        for (var index = 0; index < cases.Length; index++)
        {
            var item = cases[index];
            await auditChain.AppendAsync(
                item.Id,
                "CaseCreated",
                $"Case {item.Reference} created",
                item.CreatedBy,
                new Dictionary<string, object?>
                {
                    ["reference"] = item.Reference,
                    ["severity"] = item.Severity.ToString(),
                    ["status"] = item.Status.ToString(),
                    ["title"] = item.Title
                },
                Guid.Parse($"30000000-0000-0000-0000-{index + 1:D12}"),
                item.CreatedAt,
                cancellationToken);
        }

        await auditChain.AppendAsync(
            cases[0].Id,
            "EvidenceAdded",
            $"Evidence {seedEvidence.FileName} added",
            admin,
            new Dictionary<string, object?>
            {
                ["evidenceId"] = seedEvidence.Id,
                ["fileName"] = seedEvidence.FileName,
                ["mediaType"] = seedEvidence.MediaType,
                ["sha256"] = seedEvidence.Sha256,
                ["sizeBytes"] = seedEvidence.SizeBytes
            },
            Guid.Parse("30000000-0000-0000-0000-000000000101"),
            seedEvidence.CreatedAt,
            cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
    }

    private string? GetAdminPassword(bool demoLoginEnabled)
    {
        if (!demoLoginEnabled)
        {
            return null;
        }

        var adminPassword = configuration["Seed:AdminPassword"];
        if (!string.IsNullOrWhiteSpace(adminPassword))
        {
            return adminPassword;
        }

        if (environment.IsProduction())
        {
            throw new InvalidOperationException(
                "Seed:AdminPassword is required when demo login is enabled in Production.");
        }

        return "Admin123!";
    }

    private string? GetAnalystPassword(bool demoLoginEnabled)
    {
        if (!demoLoginEnabled)
        {
            return null;
        }

        var analystPassword = configuration["Seed:AnalystPassword"];
        if (!string.IsNullOrWhiteSpace(analystPassword))
        {
            return analystPassword;
        }

        if (environment.IsProduction())
        {
            throw new InvalidOperationException(
                "Seed:AnalystPassword is required when demo login is enabled in Production.");
        }

        return "Analyst123!";
    }

    private async Task ReconcileDemoLoginPolicyAsync(
        bool demoLoginEnabled,
        string? adminPassword,
        string? analystPassword,
        CancellationToken cancellationToken)
    {
        var demoUsers = await db.Users
            .Where(item => item.Id == AdminId || item.Id == AnalystId)
            .ToListAsync(cancellationToken);

        foreach (var user in demoUsers)
        {
            if (demoLoginEnabled)
            {
                var password = user.Id == AdminId
                    ? adminPassword!
                    : analystPassword!;
                if (!user.LocalLoginEnabled ||
                    !passwords.VerifyPassword(password, user.PasswordHash) ||
                    UsesLegacyDeterministicSalt(user))
                {
                    user.LocalLoginEnabled = true;
                    user.PasswordHash = passwords.HashPassword(password);
                }
            }
            else if (user.LocalLoginEnabled)
            {
                user.LocalLoginEnabled = false;
                user.PasswordHash = passwords.HashPassword(
                    GenerateDisabledAccountPassword());
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool UsesLegacyDeterministicSalt(User user)
    {
        var legacySeed = user.Id == AdminId
            ? "caseledger-admin"
            : "caseledger-analyst";
        var parts = user.PasswordHash.Split('$');
        if (parts.Length != 4)
        {
            return false;
        }

        try
        {
            var actualSalt = Convert.FromBase64String(parts[2]);
            var legacySalt = SHA256.HashData(
                Encoding.UTF8.GetBytes(legacySeed))[..16];
            return CryptographicOperations.FixedTimeEquals(
                actualSalt,
                legacySalt);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private async Task EnsureEntraBootstrapAdministratorAsync(
        EntraAuthenticationOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled ||
            !Guid.TryParse(options.TenantId, out var tenantId) ||
            !Guid.TryParse(
                options.BootstrapAdministratorObjectId,
                out var objectId))
        {
            return;
        }

        var administratorMappings = await db.ExternalIdentities
            .Where(
                item =>
                    item.Provider == ExternalIdentityService.MicrosoftEntraProvider &&
                    item.UserId == AdminId)
            .ToListAsync(cancellationToken);
        if (administratorMappings.Count > 0)
        {
            if (administratorMappings.Count == 1 &&
                administratorMappings[0].TenantId == tenantId &&
                administratorMappings[0].ObjectId == objectId)
            {
                return;
            }

            throw new InvalidOperationException(
                "The configured Entra bootstrap administrator does not match " +
                "the immutable administrator identity mapping in the database.");
        }

        db.ExternalIdentities.Add(new ExternalIdentity
        {
            Id = Guid.NewGuid(),
            UserId = AdminId,
            Provider = ExternalIdentityService.MicrosoftEntraProvider,
            TenantId = tenantId,
            ObjectId = objectId,
            CreatedAt = NormalizeUtcToMicroseconds(
                clock.GetUtcNow().UtcDateTime)
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string GenerateDisabledAccountPassword() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static DateTime NormalizeUtcToMicroseconds(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc
            ? value
            : value.ToUniversalTime();
        return new DateTime(
            utc.Ticks - utc.Ticks % TimeSpan.TicksPerMicrosecond,
            DateTimeKind.Utc);
    }

    private static CaseRecord NewCase(
        int id,
        string reference,
        string title,
        string summary,
        CaseStatus status,
        CaseSeverity severity,
        string category,
        User? assignee,
        User createdBy,
        DateTime createdAt,
        DateTime? dueAt,
        IReadOnlyList<string> tags)
    {
        var updatedAt = status switch
        {
            CaseStatus.Resolved => createdAt.AddDays(2),
            CaseStatus.InProgress => createdAt.AddHours(4),
            _ => createdAt
        };

        return new CaseRecord
        {
            Id = Guid.Parse($"20000000-0000-0000-0000-{id:D12}"),
            Reference = reference,
            Title = title,
            Summary = summary,
            Status = status,
            Severity = severity,
            Category = category,
            AssigneeId = assignee?.Id,
            Assignee = assignee,
            CreatedById = createdBy.Id,
            CreatedBy = createdBy,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            DueAt = dueAt,
            TagsJson = JsonSerializer.Serialize(tags)
        };
    }

    private static DateTime Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);
}
