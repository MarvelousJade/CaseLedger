namespace CaseLedger.Api.EvidenceStorage;

public readonly record struct EvidenceObjectId
{
    public EvidenceObjectId(Guid caseId, Guid evidenceId)
    {
        if (caseId == Guid.Empty)
        {
            throw new ArgumentException("Case ID cannot be empty.", nameof(caseId));
        }

        if (evidenceId == Guid.Empty)
        {
            throw new ArgumentException("Evidence ID cannot be empty.", nameof(evidenceId));
        }

        CaseId = caseId;
        EvidenceId = evidenceId;
    }

    public Guid CaseId { get; }
    public Guid EvidenceId { get; }

    public string ObjectKey
    {
        get
        {
            EnsureValid();
            return $"cases/{CaseId:N}/evidence/{EvidenceId:N}";
        }
    }

    internal void EnsureValid()
    {
        if (CaseId == Guid.Empty || EvidenceId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "Evidence object IDs must contain non-empty case and evidence IDs.");
        }
    }
}

public sealed record EvidenceObjectWriteRequest(
    EvidenceObjectId ObjectId,
    long SizeBytes,
    string MediaType,
    string Sha256);

public interface IEvidenceObjectStore
{
    Task WriteAsync(
        EvidenceObjectWriteRequest request,
        Stream content,
        CancellationToken cancellationToken);

    Task DeleteIfExistsAsync(
        EvidenceObjectId objectId,
        CancellationToken cancellationToken);
}
