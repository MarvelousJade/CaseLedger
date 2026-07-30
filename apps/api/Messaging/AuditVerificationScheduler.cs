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
            .ToListAsync(cancellationToken);
        var persistedSnapshotEvents = persistedEvents
            .Select(ToSnapshotEvent);
        var localEvents = db.ChangeTracker
            .Entries<AuditEvent>()
            .Where(entry => entry.State == EntityState.Added && entry.Entity.CaseId == caseId)
            .Select(entry => ToSnapshotEvent(entry.Entity));

        var eventsBySequence = persistedSnapshotEvents
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
        var requestedAt = UtcTimestamp.Normalize(DateTime.UtcNow);
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
        var payloadJson = JsonSerializer.Serialize(request, JsonOptions);
        var payloadBytes = Encoding.UTF8.GetByteCount(payloadJson);
        if (payloadBytes > options.Value.MaximumMessageBytes)
        {
            throw new AuditVerificationMessageTooLargeException(
                options.Value.MaximumMessageBytes,
                payloadBytes);
        }

        var outboxMessage = new OutboxMessage
        {
            Id = jobId,
            MessageType = RequestMessageType,
            PayloadJson = payloadJson,
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
            AppendFramed(hash, auditEvent.EventId);
            AppendFramed(hash, auditEvent.CaseId);
            AppendFramed(
                hash,
                auditEvent.Sequence.ToString(CultureInfo.InvariantCulture));
            AppendFramed(hash, auditEvent.EventType);
            AppendFramed(hash, auditEvent.Description);
            AppendFramed(hash, auditEvent.ActorId);
            AppendFramed(hash, auditEvent.ActorName);
            AppendFramed(hash, auditEvent.CreatedAt);
            AppendFramed(hash, auditEvent.PreviousHash);
            AppendFramed(hash, auditEvent.Hash);
            AppendFramed(hash, auditEvent.CanonicalData);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendUtf8(IncrementalHash hash, string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(value));

    private static void AppendFramed(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        AppendUtf8(hash, bytes.Length.ToString(CultureInfo.InvariantCulture));
        AppendUtf8(hash, "\n");
        hash.AppendData(bytes);
        AppendUtf8(hash, "\n");
    }

    private static AuditVerificationSnapshotEventV1 ToSnapshotEvent(
        AuditEvent auditEvent) =>
        new(
            FormatGuid(auditEvent.Id),
            FormatGuid(auditEvent.CaseId),
            auditEvent.Sequence,
            auditEvent.EventType,
            auditEvent.Description,
            FormatGuid(auditEvent.ActorId),
            auditEvent.ActorName,
            UtcTimestamp.Format(auditEvent.CreatedAt),
            auditEvent.PreviousHash,
            auditEvent.Hash,
            auditEvent.CanonicalData);

    private static string FormatGuid(Guid value) =>
        value.ToString("D").ToLowerInvariant();
}
