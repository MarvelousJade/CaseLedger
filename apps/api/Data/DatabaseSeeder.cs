using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CaseLedger.Api.Domain;
using CaseLedger.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CaseLedger.Api.Data;

public sealed class DatabaseSeeder(
    CaseLedgerDbContext db,
    PasswordService passwords,
    AuditChainService auditChain)
{
    public static readonly Guid AdminId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    public static readonly Guid AnalystId = Guid.Parse("10000000-0000-0000-0000-000000000002");

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
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
                    PasswordHash = passwords.HashPassword("Admin123!", "caseledger-admin")
                },
                new User
                {
                    Id = AnalystId,
                    Name = "Avery Singh",
                    Email = "analyst@caseledger.dev",
                    NormalizedEmail = "ANALYST@CASELEDGER.DEV",
                    Role = "Analyst",
                    PasswordHash = passwords.HashPassword("Analyst123!", "caseledger-analyst")
                });

            await db.SaveChangesAsync(cancellationToken);
        }

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
