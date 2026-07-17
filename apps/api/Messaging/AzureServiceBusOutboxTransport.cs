using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace CaseLedger.Api.Messaging;

public sealed record AzureServiceBusOutboundMessage(
    string Body,
    string ContentType,
    string CorrelationId,
    string MessageId,
    string Subject,
    int SchemaVersion);

public interface IAzureServiceBusMessageSender
{
    Task SendAsync(
        AzureServiceBusOutboundMessage message,
        CancellationToken cancellationToken);
}

public sealed class AzureServiceBusSdkMessageSender
    : IAzureServiceBusMessageSender, IAsyncDisposable
{
    private readonly ServiceBusClient client;
    private readonly ServiceBusSender sender;

    public AzureServiceBusSdkMessageSender(IOptions<MessagingOptions> options)
    {
        var settings = options.Value.AzureServiceBus;
        client = new ServiceBusClient(settings.ConnectionString!);
        sender = client.CreateSender(settings.TopicName!);
    }

    public Task SendAsync(
        AzureServiceBusOutboundMessage message,
        CancellationToken cancellationToken)
    {
        var serviceBusMessage = new ServiceBusMessage(
            new BinaryData(message.Body))
        {
            ContentType = message.ContentType,
            CorrelationId = message.CorrelationId,
            MessageId = message.MessageId,
            Subject = message.Subject
        };
        serviceBusMessage.ApplicationProperties["schemaVersion"] =
            message.SchemaVersion;
        return sender.SendMessageAsync(serviceBusMessage, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await sender.DisposeAsync();
        await client.DisposeAsync();
    }
}

public sealed class AzureServiceBusOutboxTransport(
    IAzureServiceBusMessageSender sender,
    ILogger<AzureServiceBusOutboxTransport> logger)
    : IOutboxTransport
{
    public async Task PublishAsync(
        OutboxDispatchMessage message,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                message.MessageType,
                AuditVerificationScheduler.RequestMessageType,
                StringComparison.Ordinal))
        {
            throw new OutboxTransportException(
                "UNSUPPORTED_MESSAGE_TYPE",
                "No Azure Service Bus route is registered for this message type.");
        }

        var correlationId = ReadCorrelationId(message.PayloadJson);

        var serviceBusMessage = new AzureServiceBusOutboundMessage(
            message.PayloadJson,
            "application/json",
            correlationId,
            message.Id.ToString("D"),
            message.MessageType,
            SchemaVersion: 1);

        try
        {
            await sender.SendAsync(serviceBusMessage, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Azure Service Bus publish failed with {FailureType}.",
                exception.GetType().Name);
            throw new OutboxTransportException(
                "AZURE_SERVICE_BUS_SEND_FAILED",
                "Azure Service Bus did not accept the outbox message.",
                exception);
        }
    }

    private static string ReadCorrelationId(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.TryGetProperty(
                    "correlationId",
                    out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                var correlationId = value.GetString();
                if (!string.IsNullOrWhiteSpace(correlationId) &&
                    correlationId.Length <= 128)
                {
                    return correlationId;
                }
            }
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException)
        {
            // The durable outbox retains the malformed request for diagnosis.
        }

        throw new OutboxTransportException(
            "REQUEST_PAYLOAD_INVALID",
            "The durable verification request payload is invalid.");
    }
}
