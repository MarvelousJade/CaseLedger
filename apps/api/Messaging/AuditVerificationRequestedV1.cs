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
/// SnapshotSha256 is computed across events in sequence order. Each event contributes the exact
/// UTF-8 framing: sequence\npreviousHash\nhash\nutf8ByteLength(canonicalData)\ncanonicalData\n.
/// The byte length is invariant-culture decimal and canonicalData is appended as its original
/// UTF-8 bytes, without JSON reserialization.
/// </summary>
public sealed record AuditVerificationSnapshotV1(
    int EventCount,
    IReadOnlyList<AuditVerificationSnapshotEventV1> Events);

public sealed record AuditVerificationSnapshotEventV1(
    int Sequence,
    string PreviousHash,
    string Hash,
    string CanonicalData);
