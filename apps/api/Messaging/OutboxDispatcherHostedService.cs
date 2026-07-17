using Microsoft.Extensions.Options;

namespace CaseLedger.Api.Messaging;

public sealed class OutboxDispatcherHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<MessagingOptions> options,
    ILogger<OutboxDispatcherHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollingInterval = TimeSpan.FromMilliseconds(
            Math.Clamp(options.Value.PollingIntervalMilliseconds, 100, 60_000));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor =
                    scope.ServiceProvider.GetRequiredService<OutboxDispatchProcessor>();
                var claimed = await processor.DispatchBatchAsync(stoppingToken);
                if (claimed == 0)
                {
                    await Task.Delay(pollingInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    "Outbox dispatch loop failed with {FailureType}.",
                    exception.GetType().Name);
                await Task.Delay(pollingInterval, stoppingToken);
            }
        }
    }
}
