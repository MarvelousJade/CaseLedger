using System.Text;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace CaseLedger.Api.Messaging;

public sealed class RabbitMqOutboxTransport(
    IOptions<MessagingOptions> options,
    ILogger<RabbitMqOutboxTransport> logger)
    : IOutboxTransport, IAsyncDisposable
{
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private IConnection? connection;
    private IChannel? channel;

    public async Task PublishAsync(
        OutboxDispatchMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            var publishChannel = await GetChannelAsync(cancellationToken);
            var properties = new BasicProperties
            {
                AppId = "caseledger-api",
                ContentEncoding = "utf-8",
                ContentType = "application/json",
                DeliveryMode = DeliveryModes.Persistent,
                MessageId = message.Id.ToString("D"),
                Timestamp = new AmqpTimestamp(
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                Type = message.MessageType
            };

            await publishChannel.BasicPublishAsync(
                RabbitMqTopology.AuditExchange,
                RabbitMqTopology.RoutingKeyFor(message.MessageType),
                mandatory: true,
                basicProperties: properties,
                body: Encoding.UTF8.GetBytes(message.PayloadJson),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OutboxTransportException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await ResetAsync();
            logger.LogWarning(
                "RabbitMQ publish failed with {FailureType}.",
                exception.GetType().Name);
            throw new OutboxTransportException(
                "RABBITMQ_PUBLISH_FAILED",
                "RabbitMQ did not confirm the outbox message.",
                exception);
        }
    }

    private async Task<IChannel> GetChannelAsync(
        CancellationToken cancellationToken)
    {
        if (channel is { IsOpen: true })
        {
            return channel;
        }

        await connectionGate.WaitAsync(cancellationToken);
        try
        {
            if (channel is { IsOpen: true })
            {
                return channel;
            }

            await DisposeConnectionAsync();
            var factory = RabbitMqConnectionFactory.Create(options.Value);
            connection = await factory.CreateConnectionAsync(cancellationToken);
            channel = await connection.CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true),
                cancellationToken);
            await RabbitMqTopology.DeclareAsync(channel, cancellationToken);
            return channel;
        }
        finally
        {
            connectionGate.Release();
        }
    }

    private async Task ResetAsync()
    {
        await connectionGate.WaitAsync();
        try
        {
            await DisposeConnectionAsync();
        }
        finally
        {
            connectionGate.Release();
        }
    }

    private async Task DisposeConnectionAsync()
    {
        if (channel is not null)
        {
            await channel.DisposeAsync();
            channel = null;
        }

        if (connection is not null)
        {
            await connection.DisposeAsync();
            connection = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await connectionGate.WaitAsync();
        try
        {
            await DisposeConnectionAsync();
        }
        finally
        {
            connectionGate.Release();
            connectionGate.Dispose();
        }
    }
}

internal static class RabbitMqConnectionFactory
{
    public static ConnectionFactory Create(MessagingOptions options)
    {
        var uri = options.RabbitMq.Uri;
        if (!System.Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("amqp" or "amqps"))
        {
            throw new InvalidOperationException(
                "Messaging:RabbitMq:Uri must be an absolute amqp or amqps URI.");
        }

        return new ConnectionFactory
        {
            Uri = parsed,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
            ConsumerDispatchConcurrency = 1,
            ClientProvidedName = "caseledger-api"
        };
    }
}
