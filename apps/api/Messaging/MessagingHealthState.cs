namespace CaseLedger.Api.Messaging;

public sealed class MessagingHealthState(bool required)
{
    private int brokerReady = required ? 0 : 1;

    public bool Required { get; } = required;

    public bool IsHealthy =>
        !Required || Volatile.Read(ref brokerReady) == 1;

    public void ReportBrokerReady(bool ready) =>
        Volatile.Write(ref brokerReady, ready ? 1 : 0);
}
