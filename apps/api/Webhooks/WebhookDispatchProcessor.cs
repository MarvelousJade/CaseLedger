using System.Globalization;
using CaseLedger.Api.Data;
using CaseLedger.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CaseLedger.Api.Webhooks;

public sealed class WebhookDispatchProcessor(
    CaseLedgerDbContext db,
    IWebhookTransport transport,
    IOptions<WebhookOptions> options,
    TimeProvider timeProvider,
    ILogger<WebhookDispatchProcessor> logger)
{
    public async Task<int> DispatchBatchAsync(
        CancellationToken cancellationToken = default)
    {
        var now = UtcTimestamp.Now(timeProvider);
        var candidateIds = await db.WebhookDeliveries
            .AsNoTracking()
            .Where(item =>
                item.DeliveredAt == null &&
                item.DeadLetteredAt == null &&
                item.NextAttemptAt <= now &&
                (item.LockedUntil == null || item.LockedUntil < now))
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .Take(options.Value.BatchSize)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        if (candidateIds.Count == 0)
        {
            return 0;
        }

        var lockId = Guid.NewGuid();
        var lockedUntil = now.AddSeconds(options.Value.LeaseSeconds);
        await db.WebhookDeliveries
            .Where(item =>
                candidateIds.Contains(item.Id) &&
                item.DeliveredAt == null &&
                item.DeadLetteredAt == null &&
                item.NextAttemptAt <= now &&
                (item.LockedUntil == null || item.LockedUntil < now))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.LockId, (Guid?)lockId)
                    .SetProperty(item => item.LockedUntil, (DateTime?)lockedUntil),
                cancellationToken);
        var claimed = await db.WebhookDeliveries
            .AsNoTracking()
            .Where(item => item.LockId == lockId)
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);

        foreach (var delivery in claimed)
        {
            await DispatchAsync(delivery, lockId, cancellationToken);
        }

        return claimed.Count;
    }

    private async Task DispatchAsync(
        WebhookDelivery delivery,
        Guid lockId,
        CancellationToken cancellationToken)
    {
        try
        {
            var timestamp = timeProvider
                .GetUtcNow()
                .ToUnixTimeSeconds()
                .ToString(CultureInfo.InvariantCulture);
            var signature = WebhookSignature.Create(
                options.Value.SigningSecret!,
                timestamp,
                delivery.PayloadJson);
            var response = await transport.PostAsync(
                new WebhookDispatchRequest(
                    delivery.Id,
                    delivery.EventType,
                    delivery.PayloadJson,
                    timestamp,
                    signature),
                cancellationToken);

            if (response.StatusCode is >= 200 and <= 299)
            {
                await MarkDeliveredAsync(
                    delivery,
                    lockId,
                    cancellationToken);
                return;
            }

            var retryable = response.StatusCode is 408 or 429 or >= 500 and <= 599;
            await RecordFailureAsync(
                delivery,
                lockId,
                $"WEBHOOK_HTTP_{response.StatusCode}",
                terminal: !retryable,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WebhookTransportException exception)
        {
            await RecordFailureAsync(
                delivery,
                lockId,
                exception.ErrorCode,
                terminal: false,
                cancellationToken);
        }
        catch (Exception)
        {
            await RecordFailureAsync(
                delivery,
                lockId,
                "WEBHOOK_DELIVERY_FAILED",
                terminal: false,
                cancellationToken);
        }
    }

    private Task MarkDeliveredAsync(
        WebhookDelivery delivery,
        Guid lockId,
        CancellationToken cancellationToken)
    {
        var deliveredAt = UtcTimestamp.Now(timeProvider);
        return db.WebhookDeliveries
            .Where(item =>
                item.Id == delivery.Id &&
                item.LockId == lockId &&
                item.DeliveredAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.DeliveredAt, (DateTime?)deliveredAt)
                    .SetProperty(item => item.AttemptCount, delivery.AttemptCount + 1)
                    .SetProperty(item => item.LockId, (Guid?)null)
                    .SetProperty(item => item.LockedUntil, (DateTime?)null)
                    .SetProperty(item => item.LastErrorCode, (string?)null),
                cancellationToken);
    }

    private async Task RecordFailureAsync(
        WebhookDelivery delivery,
        Guid lockId,
        string errorCode,
        bool terminal,
        CancellationToken cancellationToken)
    {
        var now = UtcTimestamp.Now(timeProvider);
        var attemptCount = delivery.AttemptCount + 1;
        var deadLettered = terminal ||
            attemptCount >= options.Value.MaxAttempts;
        var nextAttemptAt = deadLettered
            ? delivery.NextAttemptAt
            : now.AddSeconds(
                Math.Min(300, Math.Pow(2, Math.Min(attemptCount, 8))));
        var safeErrorCode = DeliveryErrorCode.IsSafe(errorCode)
            ? errorCode
            : "WEBHOOK_DELIVERY_FAILED";

        await db.WebhookDeliveries
            .Where(item => item.Id == delivery.Id && item.LockId == lockId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.AttemptCount, attemptCount)
                    .SetProperty(
                        item => item.DeadLetteredAt,
                        deadLettered ? (DateTime?)now : null)
                    .SetProperty(item => item.NextAttemptAt, nextAttemptAt)
                    .SetProperty(item => item.LockId, (Guid?)null)
                    .SetProperty(item => item.LockedUntil, (DateTime?)null)
                    .SetProperty(item => item.LastErrorCode, safeErrorCode),
                cancellationToken);
        logger.LogWarning(
            "Webhook delivery {DeliveryId} attempt {AttemptCount} failed with {ErrorCode}; dead-lettered: {DeadLettered}.",
            delivery.Id,
            attemptCount,
            safeErrorCode,
            deadLettered);
    }
}
