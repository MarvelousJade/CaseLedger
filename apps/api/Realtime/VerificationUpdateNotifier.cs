using Microsoft.AspNetCore.SignalR;

namespace CaseLedger.Api.Realtime;

public interface IVerificationUpdateNotifier
{
    Task NotifyAsync(
        VerificationUpdatedDto update,
        CancellationToken cancellationToken);
}

public sealed class SignalRVerificationUpdateNotifier(
    IHubContext<CaseUpdatesHub, ICaseUpdatesClient> hubContext)
    : IVerificationUpdateNotifier
{
    public Task NotifyAsync(
        VerificationUpdatedDto update,
        CancellationToken cancellationToken) =>
        hubContext.Clients
            .Group(CaseUpdatesHub.GroupName(update.CaseId))
            .VerificationUpdated(update);
}
