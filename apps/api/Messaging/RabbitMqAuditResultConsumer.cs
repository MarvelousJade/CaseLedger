using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace CaseLedger.Api.Messaging;

public sealed class RabbitMqAuditResultConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<MessagingOptions> options,
    ILogger<RabbitMqAuditResultConsumer> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryDelay = TimeSpan.FromSeconds(5);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeUntilStoppedAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    "RabbitMQ result consumer stopped with {FailureType}; reconnecting.",
                    exception.GetType().Name);
                await Task.Delay(retryDelay, stoppingToken);
            }
        }
    }

    private async Task ConsumeUntilStoppedAsync(CancellationToken stoppingToken)
    {
        var factory = RabbitMqConnectionFactory.Create(options.Value);
        await using var connection =
            await factory.CreateConnectionAsync(stoppingToken);
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: false,
                publisherConfirmationTrackingEnabled: false,
                consumerDispatchConcurrency: 1),
            stoppingToken);
        await RabbitMqTopology.DeclareAsync(channel, stoppingToken);
        await channel.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: 1,
            global: false,
            stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        var disconnected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.ConnectionShutdownAsync += (_, _) =>
        {
            disconnected.TrySetResult();
            return Task.CompletedTask;
        };
        channel.ChannelShutdownAsync += (_, _) =>
        {
            disconnected.TrySetResult();
            return Task.CompletedTask;
        };
        consumer.ReceivedAsync += (_, delivery) =>
            HandleDeliveryAsync(channel, delivery, stoppingToken);
        await channel.BasicConsumeAsync(
            RabbitMqTopology.ResultQueue,
            autoAck: false,
            consumer,
            stoppingToken);

        logger.LogInformation(
            "RabbitMQ audit verification result consumer is ready.");
        await disconnected.Task.WaitAsync(stoppingToken);
        throw new InvalidOperationException(
            "RabbitMQ audit verification result consumer disconnected.");
    }

    private async Task HandleDeliveryAsync(
        IChannel channel,
        BasicDeliverEventArgs delivery,
        CancellationToken stoppingToken)
    {
        try
        {
            var result = AuditVerificationResultContract.Parse(delivery.Body);
            await using var scope = scopeFactory.CreateAsyncScope();
            var handler =
                scope.ServiceProvider.GetRequiredService<AuditVerificationResultHandler>();
            await handler.HandleAsync(result, stoppingToken);
            await channel.BasicAckAsync(
                delivery.DeliveryTag,
                multiple: false,
                stoppingToken);
        }
        catch (ResultMessageValidationException exception)
        {
            logger.LogWarning(
                "RabbitMQ result rejected with {ErrorCode}.",
                exception.ErrorCode);
            await channel.BasicNackAsync(
                delivery.DeliveryTag,
                multiple: false,
                requeue: false,
                stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                "RabbitMQ result handling failed with {FailureType}; requeueing.",
                exception.GetType().Name);
            await channel.BasicNackAsync(
                delivery.DeliveryTag,
                multiple: false,
                requeue: true,
                stoppingToken);
        }
    }
}
