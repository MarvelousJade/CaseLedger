using Microsoft.AspNetCore.Mvc;

namespace CaseLedger.Api.Contracts;

public sealed record LoginRequest(string? Email, string? Password);

public sealed record AntiforgeryTokenResponse(string Token);

public sealed record AccessTokenResponse(string AccessToken, DateTime ExpiresAt);

public sealed record AuthCapabilitiesResponse(
    bool DemoLoginEnabled,
    bool EntraEnabled,
    bool ShowDemoCredentials);

public sealed record UserResponse(
    Guid Id,
    string Name,
    string Email,
    string Role);

public sealed record CreateCaseRequest(
    string? Title,
    string? Summary,
    string? Severity,
    string? Category,
    Guid? AssigneeId,
    DateTime? DueAt,
    IReadOnlyList<string>? Tags);

public sealed record UpdateCaseRequest(
    string? Title,
    string? Summary,
    string? Status,
    string? Severity,
    string? Category,
    Guid? AssigneeId,
    DateTime? DueAt,
    IReadOnlyList<string>? Tags);

public sealed record AddCommentRequest(string? Body);

public sealed record AddEvidenceRequest(
    string? FileName,
    long SizeBytes,
    string? MediaType,
    string? Sha256);

public sealed class CaseListQuery
{
    [FromQuery(Name = "search")]
    public string? Search { get; init; }

    [FromQuery(Name = "q")]
    public string? LegacySearch { get; init; }

    [FromQuery(Name = "status")]
    public string? Status { get; init; }

    [FromQuery(Name = "severity")]
    public string? Severity { get; init; }

    [FromQuery(Name = "page")]
    public int? Page { get; init; }

    [FromQuery(Name = "pageSize")]
    public int? PageSize { get; init; }

    [FromQuery(Name = "sortBy")]
    public string? SortBy { get; init; }

    [FromQuery(Name = "sortDirection")]
    public string? SortDirection { get; init; }

    [FromQuery(Name = "offset")]
    public int? LegacyOffset { get; init; }

    [FromQuery(Name = "limit")]
    public int? LegacyLimit { get; init; }
}

public sealed record CaseCollectionResponse(
    IReadOnlyList<CaseListItemResponse> Items,
    int Total,
    int Page,
    int PageSize,
    int TotalPages,
    bool HasNextPage,
    bool HasPreviousPage);

public sealed record CaseListItemResponse(
    Guid Id,
    Guid Version,
    string Reference,
    string Title,
    string Summary,
    string Status,
    string Severity,
    string Category,
    Guid? AssigneeId,
    string? AssigneeName,
    string CreatedByName,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? DueAt,
    IReadOnlyList<string> Tags);

public sealed record CaseDetailResponse(
    Guid Id,
    Guid Version,
    string Reference,
    string Title,
    string Summary,
    string Status,
    string Severity,
    string Category,
    Guid? AssigneeId,
    string? AssigneeName,
    string CreatedByName,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? DueAt,
    IReadOnlyList<string> Tags,
    IReadOnlyList<EvidenceResponse> Evidence,
    IReadOnlyList<ActivityResponse> Activity);

public sealed record EvidenceResponse(
    Guid Id,
    string FileName,
    long SizeBytes,
    string MediaType,
    string Sha256,
    string AddedByName,
    DateTime CreatedAt);

public sealed record ActivityResponse(
    Guid Id,
    string EventType,
    string Description,
    string ActorName,
    DateTime CreatedAt);

public sealed record AuditCollectionResponse(
    IReadOnlyList<AuditEventResponse> Items,
    int Total);

public sealed record AuditEventResponse(
    Guid Id,
    int Sequence,
    string EventType,
    string Description,
    string ActorName,
    DateTime CreatedAt,
    string PreviousHash,
    string Hash,
    string CanonicalData);

public sealed record AuditVerificationResponse(
    bool Valid,
    int CheckedEvents,
    int? BrokenAt = null);

public sealed record AuditVerificationJobResponse(
    Guid Id,
    string Status,
    int TargetSequence,
    string TargetHash,
    string? ResultId,
    bool? Valid,
    int? CheckedEvents,
    int? BrokenAt,
    string? ChainHead,
    string SnapshotSha256,
    string? ErrorCode,
    DateTime RequestedAt,
    DateTime? CompletedAt,
    bool IsCurrent);

public sealed record AuditExportResponse(
    Guid CaseId,
    string Reference,
    DateTime ExportedAt,
    IReadOnlyList<AuditExportEventResponse> Events);

public sealed record AuditExportEventResponse(
    int Sequence,
    string PreviousHash,
    string Hash,
    string CanonicalData);

public sealed record DashboardResponse(
    int TotalCases,
    int OpenCases,
    int CriticalCases,
    int ResolvedCases,
    string IntegrityStatus,
    IReadOnlyList<DashboardCaseResponse> RecentCases);

public sealed record DashboardCaseResponse(
    Guid Id,
    string Reference,
    string Title,
    string Status,
    string Severity,
    string? AssigneeName,
    DateTime UpdatedAt);
