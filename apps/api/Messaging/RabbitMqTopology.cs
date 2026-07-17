using RabbitMQ.Client;

namespace CaseLedger.Api.Messaging;

public static class RabbitMqTopology
{
    public const string AuditExchange = "caseledger.audit";
    public const string RetryExchange = "caseledger.audit.retry";
    public const string DeadLetterExchange = "caseledger.audit.dlx";
    public const string RequestQueue = "caseledger.audit.verify.v1";
    public const string RequestRoutingKey = "audit.verification.requested.v1";
    public const string ResultRoutingKey = "audit.verification.result.v1";
    public const string RequestDeadLetterQueue = "caseledger.audit.verify.dead.v1";
    public const string RequestDeadLetterRoutingKey = "audit.verification.dead.v1";
    public const string ResultQueue = "caseledger.api.audit-verification-results.v1";
    public const string InvalidResultRoutingKey = "audit.verification.result.invalid.v1";
    public const string InvalidResultQueue =
        "caseledger.api.audit-verification-results.dead.v1";

    private static readonly (string Queue, string RoutingKey, int DelayMilliseconds)[]
        RetryTiers =
        [
            (
                "caseledger.audit.verify.retry.5s.v1",
                "audit.verification.retry.5s.v1",
                5_000),
            (
                "caseledger.audit.verify.retry.30s.v1",
                "audit.verification.retry.30s.v1",
                30_000),
            (
                "caseledger.audit.verify.retry.5m.v1",
                "audit.verification.retry.5m.v1",
                300_000)
        ];

    public static async Task DeclareAsync(
        IChannel channel,
        CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(
            AuditExchange,
            ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: null,
            passive: false,
            noWait: false,
            cancellationToken);
        await channel.ExchangeDeclareAsync(
            RetryExchange,
            ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            arguments: null,
            passive: false,
            noWait: false,
            cancellationToken);
        await channel.ExchangeDeclareAsync(
            DeadLetterExchange,
            ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            arguments: null,
            passive: false,
            noWait: false,
            cancellationToken);
        await channel.QueueDeclareAsync(
            RequestQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = DeadLetterExchange,
                ["x-dead-letter-routing-key"] = RequestDeadLetterRoutingKey
            },
            passive: false,
            noWait: false,
            cancellationToken);
        await channel.QueueBindAsync(
            RequestQueue,
            AuditExchange,
            RequestRoutingKey,
            arguments: null,
            noWait: false,
            cancellationToken);
        foreach (var tier in RetryTiers)
        {
            await channel.QueueDeclareAsync(
                tier.Queue,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: new Dictionary<string, object?>
                {
                    ["x-message-ttl"] = tier.DelayMilliseconds,
                    ["x-dead-letter-exchange"] = AuditExchange,
                    ["x-dead-letter-routing-key"] = RequestRoutingKey
                },
                passive: false,
                noWait: false,
                cancellationToken);
            await channel.QueueBindAsync(
                tier.Queue,
                RetryExchange,
                tier.RoutingKey,
                arguments: null,
                noWait: false,
                cancellationToken);
        }

        await channel.QueueDeclareAsync(
            RequestDeadLetterQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            passive: false,
            noWait: false,
            cancellationToken);
        await channel.QueueBindAsync(
            RequestDeadLetterQueue,
            DeadLetterExchange,
            RequestDeadLetterRoutingKey,
            arguments: null,
            noWait: false,
            cancellationToken);
        await channel.QueueDeclareAsync(
            InvalidResultQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            passive: false,
            noWait: false,
            cancellationToken);
        await channel.QueueBindAsync(
            InvalidResultQueue,
            DeadLetterExchange,
            InvalidResultRoutingKey,
            arguments: null,
            noWait: false,
            cancellationToken);
        await channel.QueueDeclareAsync(
            ResultQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = DeadLetterExchange,
                ["x-dead-letter-routing-key"] = InvalidResultRoutingKey
            },
            passive: false,
            noWait: false,
            cancellationToken);
        await channel.QueueBindAsync(
            ResultQueue,
            AuditExchange,
            ResultRoutingKey,
            arguments: null,
            noWait: false,
            cancellationToken);
    }

    public static string RoutingKeyFor(string messageType) => messageType switch
    {
        AuditVerificationScheduler.RequestMessageType => RequestRoutingKey,
        _ => throw new OutboxTransportException(
            "UNSUPPORTED_MESSAGE_TYPE",
            $"No RabbitMQ route is registered for message type '{messageType}'.")
    };
}
