using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;

namespace CaseLedger.Api.Messaging;

public sealed class AzureServiceBusAuditResultConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<MessagingOptions> options,
    MessagingHealthState health,
    ILogger<AzureServiceBusAuditResultConsumer> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryDelay = TimeSpan.FromSeconds(5);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessUntilStoppedAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                health.ReportBrokerReady(false);
                logger.LogError(
                    "Azure Service Bus result consumer stopped with {FailureType}; reconnecting.",
                    exception.GetType().Name);
                await Task.Delay(retryDelay, stoppingToken);
            }
        }
    }

    private async Task ProcessUntilStoppedAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value.AzureServiceBus;
        await using var client = AzureServiceBusClientFactory.Create(settings);
        await ProbeBrokerAsync(client, settings, stoppingToken);
        await using var processor = client.CreateProcessor(
            settings.TopicName!,
            settings.ResultSubscriptionName!,
            new ServiceBusProcessorOptions
            {
                AutoCompleteMessages = false,
                MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(5),
                MaxConcurrentCalls = 1,
                PrefetchCount = 0,
                ReceiveMode = ServiceBusReceiveMode.PeekLock
            });
        processor.ProcessMessageAsync += ProcessMessageAsync;
        processor.ProcessErrorAsync += ProcessErrorAsync;

        await processor.StartProcessingAsync(stoppingToken);
        health.ReportBrokerReady(true);
        logger.LogInformation(
            "Azure Service Bus audit verification result consumer is ready.");
        try
        {
            using var probeTimer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await probeTimer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await ProbeBrokerAsync(client, settings, stoppingToken);
                    health.ReportBrokerReady(true);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    health.ReportBrokerReady(false);
                    logger.LogWarning(
                        "Azure Service Bus readiness probe failed with {FailureType}.",
                        exception.GetType().Name);
                }
            }
        }
        finally
        {
            health.ReportBrokerReady(false);
            await processor.StopProcessingAsync(CancellationToken.None);
        }
    }

    private static async Task ProbeBrokerAsync(
        ServiceBusClient client,
        AzureServiceBusOptions settings,
        CancellationToken cancellationToken)
    {
        await using var receiver = client.CreateReceiver(
            settings.TopicName!,
            settings.ResultSubscriptionName!,
            new ServiceBusReceiverOptions
            {
                ReceiveMode = ServiceBusReceiveMode.PeekLock
            });
        await using var sender = client.CreateSender(settings.TopicName!);

        _ = await receiver.PeekMessagesAsync(
            maxMessages: 1,
            fromSequenceNumber: null,
            cancellationToken: cancellationToken);
        using var batch = await sender.CreateMessageBatchAsync(cancellationToken);
    }

    private async Task ProcessMessageAsync(ProcessMessageEventArgs arguments)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var deliveryProcessor = scope.ServiceProvider
            .GetRequiredService<AzureServiceBusResultDeliveryProcessor>();
        await deliveryProcessor.ProcessAsync(
            arguments.Message.Subject,
            arguments.Message.Body.ToMemory(),
            new ServiceBusResultSettlement(arguments),
            arguments.CancellationToken);
        health.ReportBrokerReady(true);
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs arguments)
    {
        health.ReportBrokerReady(false);
        logger.LogError(
            "Azure Service Bus processor reported {FailureType} from {ErrorSource}.",
            arguments.Exception.GetType().Name,
            arguments.ErrorSource);
        return Task.CompletedTask;
    }

    private sealed class ServiceBusResultSettlement(
        ProcessMessageEventArgs arguments)
        : IAzureServiceBusResultSettlement
    {
        public Task CompleteAsync(CancellationToken cancellationToken) =>
            arguments.CompleteMessageAsync(
                arguments.Message,
                cancellationToken);

        public Task DeadLetterAsync(
            string reason,
            CancellationToken cancellationToken) =>
            arguments.DeadLetterMessageAsync(
                arguments.Message,
                reason,
                "Verification result rejected.",
                cancellationToken);

        public Task AbandonAsync(CancellationToken cancellationToken) =>
            arguments.AbandonMessageAsync(
                arguments.Message,
                propertiesToModify: null,
                cancellationToken);
    }
}
