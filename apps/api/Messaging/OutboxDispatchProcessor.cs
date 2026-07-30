using CaseLedger.Api.Data;
using CaseLedger.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CaseLedger.Api.Messaging;

public sealed record OutboxDispatchMessage(
    Guid Id,
    string MessageType,
    string PayloadJson);

public interface IOutboxTransport
{
    Task PublishAsync(
        OutboxDispatchMessage message,
        CancellationToken cancellationToken);
}

public sealed class OutboxTransportException(
    string errorCode,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string ErrorCode { get; } = errorCode;
}

public sealed class OutboxDispatchProcessor(
    CaseLedgerDbContext db,
    IOutboxTransport transport,
    IOptions<MessagingOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxDispatchProcessor> logger)
{
    public async Task<int> DispatchBatchAsync(
        CancellationToken cancellationToken = default)
    {
        var now = UtcTimestamp.Now(timeProvider);
        var batchSize = Math.Clamp(options.Value.BatchSize, 1, 100);
        var leaseSeconds = Math.Clamp(options.Value.LeaseSeconds, 5, 300);
        var candidateIds = await db.OutboxMessages
            .AsNoTracking()
            .Where(item =>
                item.PublishedAt == null &&
                item.DeadLetteredAt == null &&
                item.NextAttemptAt <= now &&
                (item.LockedUntil == null || item.LockedUntil < now))
            .OrderBy(item => item.OccurredAt)
            .ThenBy(item => item.Id)
            .Take(batchSize)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        if (candidateIds.Count == 0)
        {
            return 0;
        }

        var lockId = Guid.NewGuid();
        var lockedUntil = now.AddSeconds(leaseSeconds);
        await db.OutboxMessages
            .Where(item =>
                candidateIds.Contains(item.Id) &&
                item.PublishedAt == null &&
                item.DeadLetteredAt == null &&
                item.NextAttemptAt <= now &&
                (item.LockedUntil == null || item.LockedUntil < now))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.LockId, (Guid?)lockId)
                    .SetProperty(item => item.LockedUntil, (DateTime?)lockedUntil),
                cancellationToken);
        var claimed = await db.OutboxMessages
            .AsNoTracking()
            .Where(item => item.LockId == lockId)
            .OrderBy(item => item.OccurredAt)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);

        foreach (var message in claimed)
        {
            try
            {
                await transport.PublishAsync(
                    new OutboxDispatchMessage(
                        message.Id,
                        message.MessageType,
                        message.PayloadJson),
                    cancellationToken);
                var publishedAt = UtcTimestamp.Now(timeProvider);
                await db.OutboxMessages
                    .Where(item =>
                        item.Id == message.Id &&
                        item.LockId == lockId &&
                        item.PublishedAt == null)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(item => item.PublishedAt, (DateTime?)publishedAt)
                            .SetProperty(item => item.AttemptCount, message.AttemptCount + 1)
                            .SetProperty(item => item.LockId, (Guid?)null)
                            .SetProperty(item => item.LockedUntil, (DateTime?)null)
                            .SetProperty(item => item.LastErrorCode, (string?)null),
                        cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await RecordFailureAsync(message, lockId, exception, cancellationToken);
            }
        }

        return claimed.Count;
    }

    private async Task RecordFailureAsync(
        OutboxMessage message,
        Guid lockId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var now = UtcTimestamp.Now(timeProvider);
        var attemptCount = message.AttemptCount + 1;
        var maxAttempts = Math.Clamp(options.Value.MaxAttempts, 1, 100);
        var deadLettered = attemptCount >= maxAttempts;
        var nextAttemptAt = deadLettered
            ? message.NextAttemptAt
            : now.AddSeconds(Math.Min(300, Math.Pow(2, Math.Min(attemptCount, 8))));
        var errorCode = exception is OutboxTransportException transportException
            ? transportException.ErrorCode
            : "PUBLISH_FAILED";
        if (!DeliveryErrorCode.IsSafe(errorCode))
        {
            errorCode = "PUBLISH_FAILED";
        }

        await db.OutboxMessages
            .Where(item => item.Id == message.Id && item.LockId == lockId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.AttemptCount, attemptCount)
                    .SetProperty(
                        item => item.DeadLetteredAt,
                        deadLettered ? (DateTime?)now : null)
                    .SetProperty(item => item.NextAttemptAt, nextAttemptAt)
                    .SetProperty(item => item.LockId, (Guid?)null)
                    .SetProperty(item => item.LockedUntil, (DateTime?)null)
                    .SetProperty(item => item.LastErrorCode, errorCode),
                cancellationToken);
        logger.LogWarning(
            "Outbox message {MessageId} publish attempt {AttemptCount} failed with {ErrorCode}; dead-lettered: {DeadLettered}.",
            message.Id,
            attemptCount,
            errorCode,
            deadLettered);
    }
}
