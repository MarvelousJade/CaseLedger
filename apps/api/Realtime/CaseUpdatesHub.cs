using CaseLedger.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CaseLedger.Api.Realtime;

public sealed record VerificationUpdatedDto(
    Guid JobId,
    Guid CaseId,
    string ResultId,
    string Status,
    bool? Valid,
    int? CheckedEvents,
    int? BrokenAt);

public interface ICaseUpdatesClient
{
    Task VerificationUpdated(VerificationUpdatedDto update);
}

[Authorize]
public sealed class CaseUpdatesHub(CaseLedgerDbContext db) : Hub<ICaseUpdatesClient>
{
    public async Task SubscribeCase(Guid caseId)
    {
        if (!await db.Cases.AsNoTracking().AnyAsync(
                item => item.Id == caseId,
                Context.ConnectionAborted))
        {
            throw new HubException("Case not found.");
        }

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            GroupName(caseId),
            Context.ConnectionAborted);
    }

    public Task UnsubscribeCase(Guid caseId) =>
        Groups.RemoveFromGroupAsync(
            Context.ConnectionId,
            GroupName(caseId),
            Context.ConnectionAborted);

    public static string GroupName(Guid caseId) => $"case:{caseId:D}";
}
