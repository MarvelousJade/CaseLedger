using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CaseLedger.Api.Data;
using CaseLedger.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CaseLedger.Api.Messaging;

public sealed class AuditVerificationScheduler(
    CaseLedgerDbContext db,
    IOptions<MessagingOptions> options)
{
    public const string RequestMessageType = "caseledger.audit.verification.requested.v1";
    public const string VerificationProfile = "export-chain-v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool IsEnabled => options.Value.Enabled;

    public async Task<AuditVerificationJob> ScheduleAsync(
        Guid caseId,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            throw new InvalidOperationException("Verification messaging is disabled.");
        }

        var caseRecord = db.Cases.Local.FirstOrDefault(item => item.Id == caseId) ??
            await db.Cases.SingleAsync(item => item.Id == caseId, cancellationToken);
        var persistedEvents = await db.AuditEvents
            .AsNoTracking()
            .Where(item => item.CaseId == caseId)
            .OrderBy(item => item.Sequence)
            .Select(item => new AuditVerificationSnapshotEventV1(
                item.Sequence,
                item.PreviousHash,
                item.Hash,
                item.CanonicalData))
            .ToListAsync(cancellationToken);
        var localEvents = db.ChangeTracker
            .Entries<AuditEvent>()
            .Where(entry => entry.State == EntityState.Added && entry.Entity.CaseId == caseId)
            .Select(entry => new AuditVerificationSnapshotEventV1(
                entry.Entity.Sequence,
                entry.Entity.PreviousHash,
                entry.Entity.Hash,
                entry.Entity.CanonicalData));

        var eventsBySequence = persistedEvents
            .Concat(localEvents)
            .GroupBy(item => item.Sequence)
            .Select(group => group.Last())
            .OrderBy(item => item.Sequence)
            .ToArray();
        var snapshot = new AuditVerificationSnapshotV1(eventsBySequence.Length, eventsBySequence);
        var observedHash = eventsBySequence.Length == 0
            ? CaseRecord.GenesisAuditHash
            : eventsBySequence[^1].Hash;
        if (eventsBySequence.Length != caseRecord.AuditHeadSequence ||
            (eventsBySequence.Length > 0 &&
             eventsBySequence[^1].Sequence != caseRecord.AuditHeadSequence) ||
            !string.Equals(observedHash, caseRecord.AuditHeadHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The audit snapshot does not match the case's stored audit head.");
        }

        var snapshotSha256 = ComputeSnapshotSha256(eventsBySequence);
        var requestedAt = NormalizeUtcToMicroseconds(DateTime.UtcNow);
        var jobId = Guid.NewGuid();
        var effectiveCorrelationId = string.IsNullOrWhiteSpace(correlationId)
            ? Activity.Current?.TraceId.ToString() ?? jobId.ToString("N")
            : correlationId.Trim();
        var job = new AuditVerificationJob
        {
            Id = jobId,
            CaseId = caseRecord.Id,
            Case = caseRecord,
            Status = AuditVerificationJobStatus.Queued,
            TargetSequence = caseRecord.AuditHeadSequence,
            TargetHash = caseRecord.AuditHeadHash,
            SnapshotSha256 = snapshotSha256,
            RequestedAt = requestedAt
        };
        var request = new AuditVerificationRequestedV1(
            1,
            RequestMessageType,
            jobId,
            jobId,
            effectiveCorrelationId,
            requestedAt,
            new AuditVerificationRequestedDataV1(
                caseRecord.Id,
                VerificationProfile,
                job.TargetSequence,
                job.TargetHash,
                snapshot));
        var outboxMessage = new OutboxMessage
        {
            Id = jobId,
            MessageType = RequestMessageType,
            PayloadJson = JsonSerializer.Serialize(request, JsonOptions),
            OccurredAt = requestedAt,
            NextAttemptAt = requestedAt
        };

        db.AuditVerificationJobs.Add(job);
        db.OutboxMessages.Add(outboxMessage);
        return job;
    }

    public static string ComputeSnapshotSha256(
        IReadOnlyList<AuditVerificationSnapshotEventV1> events)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var auditEvent in events.OrderBy(item => item.Sequence))
        {
            AppendUtf8(hash, auditEvent.Sequence.ToString(CultureInfo.InvariantCulture));
            AppendUtf8(hash, "\n");
            AppendUtf8(hash, auditEvent.PreviousHash);
            AppendUtf8(hash, "\n");
            AppendUtf8(hash, auditEvent.Hash);
            AppendUtf8(hash, "\n");
            var canonicalData = Encoding.UTF8.GetBytes(auditEvent.CanonicalData);
            AppendUtf8(hash, canonicalData.Length.ToString(CultureInfo.InvariantCulture));
            AppendUtf8(hash, "\n");
            hash.AppendData(canonicalData);
            AppendUtf8(hash, "\n");
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendUtf8(IncrementalHash hash, string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(value));

    private static DateTime NormalizeUtcToMicroseconds(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc
            ? value
            : value.ToUniversalTime();
        return new DateTime(
            utc.Ticks - utc.Ticks % TimeSpan.TicksPerMicrosecond,
            DateTimeKind.Utc);
    }
}
