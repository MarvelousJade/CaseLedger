using System.Text;

namespace CaseLedger.Api.Webhooks;

public sealed record WebhookOptions
{
    public const string SectionName = "Webhook";

    public bool Enabled { get; init; }
    public string? DestinationUrl { get; init; }
    public string? SigningSecret { get; init; }
    public bool AllowInsecureHttp { get; init; }
    public int PollingIntervalMilliseconds { get; init; } = 1_000;
    public int BatchSize { get; init; } = 20;
    public int LeaseSeconds { get; init; } = 30;
    public int MaxAttempts { get; init; } = 8;
    public int TimeoutSeconds { get; init; } = 10;
}

public static class WebhookRuntimeOptions
{
    public static Uri Validate(WebhookOptions options)
    {
        if (!Uri.TryCreate(
                options.DestinationUrl,
                UriKind.Absolute,
                out var destination) ||
            destination.UserInfo.Length != 0 ||
            destination.Fragment.Length != 0 ||
            destination.Scheme is not ("https" or "http"))
        {
            throw InvalidConfiguration();
        }

        if (destination.Scheme == "http" &&
            !options.AllowInsecureHttp)
        {
            throw InvalidConfiguration();
        }

        var secretLength = options.SigningSecret is null
            ? 0
            : Encoding.UTF8.GetByteCount(options.SigningSecret);
        if (secretLength is < 32 or > 1_024 ||
            options.PollingIntervalMilliseconds is < 100 or > 60_000 ||
            options.BatchSize is < 1 or > 100 ||
            options.LeaseSeconds is < 5 or > 300 ||
            options.MaxAttempts is < 1 or > 100 ||
            options.TimeoutSeconds is < 1 or > 120)
        {
            throw InvalidConfiguration();
        }

        return destination;
    }

    private static InvalidOperationException InvalidConfiguration() =>
        new(
            "Webhook configuration requires a fixed HTTPS destination, a signing secret, " +
            "and valid delivery limits. Insecure HTTP requires explicit opt-in.");
}
