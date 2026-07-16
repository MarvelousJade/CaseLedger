using CaseLedger.Api.Contracts;
using CaseLedger.Api.Data;
using CaseLedger.Api.Domain;
using CaseLedger.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CaseLedger.Api.GraphQL;

public sealed class DashboardQuery
{
    public async Task<DashboardResponse> GetDashboardAsync(
        [Service] CaseLedgerDbContext db,
        [Service] AuditChainService auditChain,
        CancellationToken cancellationToken)
    {
        var totalCases = await db.Cases.CountAsync(cancellationToken);
        var openCases = await db.Cases.CountAsync(
            item => item.Status != CaseStatus.Resolved,
            cancellationToken);
        var criticalCases = await db.Cases.CountAsync(
            item => item.Severity == CaseSeverity.Critical,
            cancellationToken);
        var resolvedCases = await db.Cases.CountAsync(
            item => item.Status == CaseStatus.Resolved,
            cancellationToken);

        var recent = await db.Cases
            .AsNoTracking()
            .Include(item => item.Assignee)
            .OrderByDescending(item => item.UpdatedAt)
            .Take(5)
            .Select(item => new DashboardCaseResponse(
                item.Id,
                item.Reference,
                item.Title,
                item.Status.ToString(),
                item.Severity.ToString(),
                item.Assignee == null ? null : item.Assignee.Name,
                item.UpdatedAt))
            .ToListAsync(cancellationToken);

        var integrityStatus = "verified";
        var caseIds = await db.Cases
            .AsNoTracking()
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        foreach (var caseId in caseIds)
        {
            var verification = await auditChain.VerifyAsync(caseId, cancellationToken);
            if (!verification.Valid)
            {
                integrityStatus = "compromised";
                break;
            }
        }

        return new DashboardResponse(
            totalCases,
            openCases,
            criticalCases,
            resolvedCases,
            integrityStatus,
            recent);
    }
}
