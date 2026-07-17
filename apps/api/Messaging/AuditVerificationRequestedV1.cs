namespace CaseLedger.Api.Messaging;

public sealed record AuditVerificationRequestedV1(
    int SchemaVersion,
    string MessageType,
    Guid MessageId,
    Guid JobId,
    string CorrelationId,
    DateTime RequestedAt,
    AuditVerificationRequestedDataV1 Data);

public sealed record AuditVerificationRequestedDataV1(
    Guid CaseId,
    string VerificationProfile,
    int TargetSequence,
    string TargetHash,
    AuditVerificationSnapshotV1 Snapshot);

/// <summary>
/// SnapshotSha256 is computed across events in sequence order. Every field contributes its
/// invariant-culture UTF-8 byte length, a newline, the original UTF-8 bytes, and a final newline.
/// Field order is eventId, caseId, sequence, eventType, description, actorId, actorName,
/// createdAt, previousHash, hash, canonicalData.
/// </summary>
public sealed record AuditVerificationSnapshotV1(
    int EventCount,
    IReadOnlyList<AuditVerificationSnapshotEventV1> Events);

public sealed record AuditVerificationSnapshotEventV1(
    string EventId,
    string CaseId,
    int Sequence,
    string EventType,
    string Description,
    string ActorId,
    string ActorName,
    string CreatedAt,
    string PreviousHash,
    string Hash,
    string CanonicalData);
