using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;

namespace CaseLedger.Api.Messaging;

public sealed class AzureServiceBusAuditResultConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<MessagingOptions> options,
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
        await using var client = new ServiceBusClient(settings.ConnectionString!);
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
        logger.LogInformation(
            "Azure Service Bus audit verification result consumer is ready.");
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        finally
        {
            await processor.StopProcessingAsync(CancellationToken.None);
        }
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
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs arguments)
    {
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
