namespace CaseLedger.Api.Messaging;

public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    public bool Enabled { get; init; }
    public string Provider { get; init; } = "RabbitMq";
    public RabbitMqOptions RabbitMq { get; init; } = new();
    public int PollingIntervalMilliseconds { get; init; } = 1_000;
    public int BatchSize { get; init; } = 20;
    public int LeaseSeconds { get; init; } = 30;
    public int MaxAttempts { get; init; } = 8;
}

public sealed class RabbitMqOptions
{
    public string? Uri { get; init; }
}
