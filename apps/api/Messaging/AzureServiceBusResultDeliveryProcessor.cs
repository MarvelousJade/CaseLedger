namespace CaseLedger.Api.Messaging;

public interface IAzureServiceBusResultSettlement
{
    Task CompleteAsync(CancellationToken cancellationToken);
    Task DeadLetterAsync(
        string reason,
        CancellationToken cancellationToken);
    Task AbandonAsync(CancellationToken cancellationToken);
}

public sealed class AzureServiceBusResultDeliveryProcessor(
    IAuditVerificationResultApplier applier,
    ILogger<AzureServiceBusResultDeliveryProcessor> logger)
{
    public async Task ProcessAsync(
        string? subject,
        ReadOnlyMemory<byte> body,
        IAzureServiceBusResultSettlement settlement,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!string.Equals(
                    subject,
                    AuditVerificationResultContract.MessageType,
                    StringComparison.Ordinal))
            {
                throw new ResultMessageValidationException(
                    "RESULT_SUBJECT_INVALID",
                    "The Service Bus message subject is invalid.");
            }

            var result = AuditVerificationResultContract.Parse(body);
            await applier.HandleAsync(result, cancellationToken);
        }
        catch (ResultMessageValidationException exception)
        {
            var reason = SanitizeReason(exception.ErrorCode);
            logger.LogWarning(
                "Azure Service Bus result rejected with {ErrorCode}.",
                reason);
            await DeadLetterOrAbandonAsync(
                settlement,
                reason,
                cancellationToken);
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Azure Service Bus result handling failed with {FailureType}; abandoning.",
                exception.GetType().Name);
            await TryAbandonAsync(settlement, cancellationToken);
            return;
        }

        try
        {
            await settlement.CompleteAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Azure Service Bus completion failed with {FailureType}; abandoning.",
                exception.GetType().Name);
            await TryAbandonAsync(settlement, cancellationToken);
        }
    }

    private async Task DeadLetterOrAbandonAsync(
        IAzureServiceBusResultSettlement settlement,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            await settlement.DeadLetterAsync(reason, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Azure Service Bus dead-letter settlement failed with {FailureType}; abandoning.",
                exception.GetType().Name);
            await TryAbandonAsync(settlement, cancellationToken);
        }
    }

    private async Task TryAbandonAsync(
        IAzureServiceBusResultSettlement settlement,
        CancellationToken cancellationToken)
    {
        try
        {
            await settlement.AbandonAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Azure Service Bus abandon settlement failed with {FailureType}.",
                exception.GetType().Name);
        }
    }

    private static string SanitizeReason(string? value)
    {
        if (value is null ||
            value.Length is < 1 or > 80 ||
            value.Any(character =>
                character is not (>= 'A' and <= 'Z') &&
                character is not (>= '0' and <= '9') &&
                character != '_'))
        {
            return "RESULT_REJECTED";
        }

        return value;
    }
}
