using System.Text.Json;
using CaseLedger.Api.Contracts;
using CaseLedger.Api.Data;
using CaseLedger.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace CaseLedger.Api.Endpoints;

internal static class CaseMappings
{
    public static CaseListItemResponse ToListItem(CaseRecord item) =>
        new(
            item.Id,
            item.Reference,
            item.Title,
            item.Summary,
            item.Status.ToString(),
            item.Severity.ToString(),
            item.Category,
            item.AssigneeId,
            item.Assignee?.Name,
            item.CreatedBy.Name,
            item.CreatedAt,
            item.UpdatedAt,
            item.DueAt,
            ReadTags(item.TagsJson));

    public static async Task<CaseDetailResponse?> LoadDetailAsync(
        CaseLedgerDbContext db,
        Guid caseId,
        CancellationToken cancellationToken)
    {
        var item = await db.Cases
            .AsNoTracking()
            .AsSplitQuery()
            .Include(caseRecord => caseRecord.Assignee)
            .Include(caseRecord => caseRecord.CreatedBy)
            .Include(caseRecord => caseRecord.Evidence)
                .ThenInclude(evidence => evidence.AddedBy)
            .Include(caseRecord => caseRecord.AuditEvents)
            .SingleOrDefaultAsync(caseRecord => caseRecord.Id == caseId, cancellationToken);

        return item is null ? null : ToDetail(item);
    }

    public static EvidenceResponse ToEvidence(Evidence evidence) =>
        new(
            evidence.Id,
            evidence.FileName,
            evidence.SizeBytes,
            evidence.MediaType,
            evidence.Sha256,
            evidence.AddedBy.Name,
            evidence.CreatedAt);

    public static ActivityResponse ToActivity(AuditEvent auditEvent) =>
        new(
            auditEvent.Id,
            auditEvent.EventType,
            auditEvent.Description,
            auditEvent.ActorName,
            auditEvent.CreatedAt);

    public static IReadOnlyList<string> NormalizeTags(IReadOnlyList<string>? tags)
    {
        return tags is null
            ? []
            : tags
                .Select(tag => tag.Trim())
                .Where(tag => tag.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToArray();
    }

    private static CaseDetailResponse ToDetail(CaseRecord item) =>
        new(
            item.Id,
            item.Reference,
            item.Title,
            item.Summary,
            item.Status.ToString(),
            item.Severity.ToString(),
            item.Category,
            item.AssigneeId,
            item.Assignee?.Name,
            item.CreatedBy.Name,
            item.CreatedAt,
            item.UpdatedAt,
            item.DueAt,
            ReadTags(item.TagsJson),
            item.Evidence
                .OrderByDescending(evidence => evidence.CreatedAt)
                .Select(ToEvidence)
                .ToArray(),
            item.AuditEvents
                .OrderByDescending(auditEvent => auditEvent.Sequence)
                .Select(ToActivity)
                .ToArray());

    private static IReadOnlyList<string> ReadTags(string tagsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(tagsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
