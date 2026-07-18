using Microsoft.AspNetCore.Mvc;

namespace CaseLedger.Api.Contracts;

public sealed class OperationalFailureListQuery
{
    [FromQuery(Name = "kind")]
    public string? Kind { get; init; }

    [FromQuery(Name = "page")]
    public int Page { get; init; } = 1;

    [FromQuery(Name = "pageSize")]
    public int PageSize { get; init; } = 50;
}

public sealed record OperationalFailureCollectionResponse(
    IReadOnlyList<OperationalFailureResponse> Items,
    int Total,
    int Page,
    int PageSize,
    int TotalPages);

public sealed record OperationalFailureResponse(
    string Kind,
    Guid Id,
    Guid? CaseId,
    string? CaseReference,
    Guid? VerificationJobId,
    DateTime OccurredAt,
    DateTime? DeadLetteredAt,
    int? AttemptCount,
    string? ErrorCode,
    bool Replayable,
    string? ReplayBlockedReason);

public sealed record OperationalReplayRequest(
    DateTime? DeadLetteredAt,
    string? Reason);

public sealed record OperationalReplayResponse(
    Guid ReplayId,
    string Kind,
    Guid SourceId,
    DateTime SourceDeadLetteredAt,
    DateTime ReplayedAt);
