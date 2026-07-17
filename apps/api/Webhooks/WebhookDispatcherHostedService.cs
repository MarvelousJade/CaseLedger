using Microsoft.Extensions.Options;

namespace CaseLedger.Api.Webhooks;

public sealed class WebhookDispatcherHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<WebhookOptions> options,
    ILogger<WebhookDispatcherHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollingInterval = TimeSpan.FromMilliseconds(
            options.Value.PollingIntervalMilliseconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor =
                    scope.ServiceProvider.GetRequiredService<WebhookDispatchProcessor>();
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
                    "Webhook dispatch loop failed with {FailureType}.",
                    exception.GetType().Name);
                await Task.Delay(pollingInterval, stoppingToken);
            }
        }
    }
}
