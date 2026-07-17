using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CaseLedger.Api.Contracts;
using CaseLedger.Api.Data;
using CaseLedger.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace CaseLedger.Api.Services;

public sealed class AuditChainService(
    CaseLedgerDbContext db,
    CaseLedgerTelemetry telemetry)
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        WriteIndented = false
    };

    public async Task<AuditEvent> AppendAsync(
        Guid caseId,
        string eventType,
        string description,
        User actor,
        IReadOnlyDictionary<string, object?>? data = null,
        Guid? eventId = null,
        DateTime? createdAt = null,
        CancellationToken cancellationToken = default)
    {
        var localPrevious = db.AuditEvents.Local
            .Where(item => item.CaseId == caseId)
            .OrderByDescending(item => item.Sequence)
            .FirstOrDefault();

        var persistedPrevious = await db.AuditEvents
            .AsNoTracking()
            .Where(item => item.CaseId == caseId)
            .OrderByDescending(item => item.Sequence)
            .Select(item => new PreviousAudit(item.Sequence, item.Hash))
            .FirstOrDefaultAsync(cancellationToken);

        var sequence = 1;
        var previousHash = GenesisHash;
        if (persistedPrevious is not null)
        {
            sequence = persistedPrevious.Sequence + 1;
            previousHash = persistedPrevious.Hash;
        }

        if (localPrevious is not null && localPrevious.Sequence >= sequence)
        {
            sequence = localPrevious.Sequence + 1;
            previousHash = localPrevious.Hash;
        }

        var id = eventId ?? Guid.NewGuid();
        var timestamp = NormalizeUtc(createdAt ?? DateTime.UtcNow);
        var canonicalData = CreateCanonicalData(
            id,
            caseId,
            sequence,
            eventType,
            description,
            actor,
            timestamp,
            data);

        var auditEvent = new AuditEvent
        {
            Id = id,
            CaseId = caseId,
            Sequence = sequence,
            EventType = eventType,
            Description = description,
            ActorId = actor.Id,
            Actor = actor,
            ActorName = actor.Name,
            CreatedAt = timestamp,
            PreviousHash = previousHash,
            CanonicalData = canonicalData,
            Hash = ComputeHash(previousHash, canonicalData)
        };

        db.AuditEvents.Add(auditEvent);
        return auditEvent;
    }

    public void RecordAppendCommitted(AuditEvent auditEvent) =>
        telemetry.RecordAuditEventAppended(
            auditEvent.CaseId,
            auditEvent.Sequence,
            auditEvent.EventType);

    public async Task<AuditVerificationResponse> VerifyAsync(
        Guid caseId,
        CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.StartCaseOperation("caseledger.audit.verify", caseId);
        var stopwatch = Stopwatch.StartNew();

        AuditVerificationResponse Complete(AuditVerificationResponse response)
        {
            stopwatch.Stop();
            activity?.SetTag("caseledger.audit.result", response.Valid ? "valid" : "invalid");
            activity?.SetTag("caseledger.audit.checked_events", response.CheckedEvents);
            if (response.BrokenAt is not null)
            {
                activity?.SetTag("caseledger.audit.broken_at", response.BrokenAt.Value);
            }

            activity?.SetStatus(ActivityStatusCode.Ok);
            telemetry.RecordAuditVerification(
                caseId,
                response.Valid,
                response.CheckedEvents,
                response.BrokenAt,
                stopwatch.Elapsed);
            return response;
        }

        var events = await db.AuditEvents
            .AsNoTracking()
            .Where(item => item.CaseId == caseId)
            .OrderBy(item => item.Sequence)
            .ToListAsync(cancellationToken);

        var previousHash = GenesisHash;
        var expectedSequence = 1;
        var checkedEvents = 0;

        foreach (var auditEvent in events)
        {
            checkedEvents++;
            var valid = auditEvent.Sequence == expectedSequence &&
                        string.Equals(auditEvent.PreviousHash, previousHash, StringComparison.Ordinal) &&
                        string.Equals(
                            auditEvent.Hash,
                            ComputeHash(auditEvent.PreviousHash, auditEvent.CanonicalData),
                            StringComparison.Ordinal) &&
                        CanonicalDataMatches(auditEvent);

            if (!valid)
            {
                return Complete(new AuditVerificationResponse(
                    false,
                    checkedEvents,
                    auditEvent.Sequence));
            }

            previousHash = auditEvent.Hash;
            expectedSequence++;
        }

        return Complete(new AuditVerificationResponse(true, checkedEvents));
    }

    public static string ComputeHash(string previousHash, string canonicalData)
    {
        var bytes = Encoding.UTF8.GetBytes($"{previousHash}\n{canonicalData}");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string CreateCanonicalData(
        Guid eventId,
        Guid caseId,
        int sequence,
        string eventType,
        string description,
        User actor,
        DateTime createdAt,
        IReadOnlyDictionary<string, object?>? data)
    {
        var normalizedData = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        if (data is not null)
        {
            foreach (var pair in data.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                normalizedData[pair.Key] = NormalizeValue(pair.Value);
            }
        }

        var payload = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["actorId"] = actor.Id.ToString("D"),
            ["actorName"] = actor.Name,
            ["caseId"] = caseId.ToString("D"),
            ["createdAt"] = FormatUtc(createdAt),
            ["data"] = normalizedData,
            ["description"] = description,
            ["eventId"] = eventId.ToString("D"),
            ["eventType"] = eventType,
            ["sequence"] = sequence,
            ["version"] = 1
        };

        return JsonSerializer.Serialize(payload, CanonicalJsonOptions);
    }

    private static object? NormalizeValue(object? value)
    {
        return value switch
        {
            null => null,
            IReadOnlyDictionary<string, object?> dictionary => dictionary
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => NormalizeValue(pair.Value), StringComparer.Ordinal),
            IEnumerable<string> strings => strings.ToArray(),
            DateTime timestamp => FormatUtc(timestamp),
            Guid id => id.ToString("D"),
            _ => value
        };
    }

    private static bool CanonicalDataMatches(AuditEvent auditEvent)
    {
        try
        {
            using var document = JsonDocument.Parse(auditEvent.CanonicalData);
            var root = document.RootElement;

            return root.GetProperty("version").GetInt32() == 1 &&
                   root.GetProperty("eventId").GetString() == auditEvent.Id.ToString("D") &&
                   root.GetProperty("caseId").GetString() == auditEvent.CaseId.ToString("D") &&
                   root.GetProperty("sequence").GetInt32() == auditEvent.Sequence &&
                   root.GetProperty("eventType").GetString() == auditEvent.EventType &&
                   root.GetProperty("description").GetString() == auditEvent.Description &&
                   root.GetProperty("actorId").GetString() == auditEvent.ActorId.ToString("D") &&
                   root.GetProperty("actorName").GetString() == auditEvent.ActorName &&
                   root.GetProperty("createdAt").GetString() == FormatUtc(auditEvent.CreatedAt) &&
                   root.GetProperty("data").ValueKind == JsonValueKind.Object;
        }
        catch (Exception exception) when (
            exception is JsonException or System.Collections.Generic.KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static DateTime NormalizeUtc(DateTime value)
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

    private static string FormatUtc(DateTime value) =>
        NormalizeUtc(value).ToString("O", CultureInfo.InvariantCulture);

    private sealed record PreviousAudit(int Sequence, string Hash);
}
