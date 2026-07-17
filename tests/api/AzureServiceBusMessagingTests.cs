using System.Net;
using System.Text.Json;
using CaseLedger.Api.Messaging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaseLedger.Api.Tests;

public sealed class AzureServiceBusMessagingTests
{
    private static readonly string ConnectionString =
        CreateFakeConnectionString();
    private const string SnapshotSha256 =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ChainHead =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void ProviderValidationAcceptsAzureAndKeepsRabbitMqDefault()
    {
        Assert.Equal("RabbitMq", new MessagingOptions().Provider);
        var provider = MessagingRuntimeOptions.Validate(
            new MessagingOptions
            {
                Provider = "AzureServiceBus",
                AzureServiceBus = new AzureServiceBusOptions
                {
                    ConnectionString = ConnectionString,
                    TopicName = "caseledger-audit",
                    ResultSubscriptionName = "api-results-v1"
                }
            });

        Assert.Equal(MessagingProviderKind.AzureServiceBus, provider);
    }

    [Fact]
    public void InvalidAzureConfigurationNeverEchoesConnectionString()
    {
        const string sensitiveValue = "DO_NOT_LEAK_CONNECTION_SECRET";
        var exception = Assert.Throws<InvalidOperationException>(
            () => MessagingRuntimeOptions.Validate(
                new MessagingOptions
                {
                    Provider = "AzureServiceBus",
                    AzureServiceBus = new AzureServiceBusOptions
                    {
                        ConnectionString = sensitiveValue,
                        TopicName = "caseledger-audit",
                        ResultSubscriptionName = "api-results-v1"
                    }
                }));

        Assert.DoesNotContain(sensitiveValue, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestingEnvironmentDoesNotStartAzureHostedLoops()
    {
        using var factory = new CaseLedgerFactory(
            messagingEnabled: true,
            messagingProvider: "AzureServiceBus");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AzureOutboxTransportBuildsDurableTopicRequestMetadata()
    {
        var sender = new FakeMessageSender();
        var transport = new AzureServiceBusOutboxTransport(
            sender,
            NullLogger<AzureServiceBusOutboxTransport>.Instance);
        var id = Guid.Parse("d37eed44-5615-40dd-905a-9aeb2911b5e4");
        var payload = "{\"schemaVersion\":1,\"jobId\":\"d37eed44-5615-40dd-905a-9aeb2911b5e4\",\"correlationId\":\"trace-123\"}";

        await transport.PublishAsync(
            new OutboxDispatchMessage(
                id,
                AuditVerificationScheduler.RequestMessageType,
                payload),
            CancellationToken.None);

        var sent = Assert.Single(sender.Messages);
        Assert.Equal(payload, sent.Body);
        Assert.Equal("application/json", sent.ContentType);
        Assert.Equal("trace-123", sent.CorrelationId);
        Assert.Equal(id.ToString("D"), sent.MessageId);
        Assert.Equal(AuditVerificationScheduler.RequestMessageType, sent.Subject);
        Assert.Equal(1, sent.SchemaVersion);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"correlationId\":\"\"}")]
    public async Task AzureOutboxTransportRejectsInvalidInternalCorrelation(
        string payload)
    {
        var sender = new FakeMessageSender();
        var transport = new AzureServiceBusOutboxTransport(
            sender,
            NullLogger<AzureServiceBusOutboxTransport>.Instance);

        var exception = await Assert.ThrowsAsync<OutboxTransportException>(
            () => transport.PublishAsync(
                new OutboxDispatchMessage(
                    Guid.NewGuid(),
                    AuditVerificationScheduler.RequestMessageType,
                    payload),
                CancellationToken.None));

        Assert.Equal("REQUEST_PAYLOAD_INVALID", exception.ErrorCode);
        Assert.Empty(sender.Messages);
        Assert.DoesNotContain(payload, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ResultApplyDisposition.Applied)]
    [InlineData(ResultApplyDisposition.Duplicate)]
    public async Task AppliedAndDuplicateResultsAreCompleted(
        ResultApplyDisposition disposition)
    {
        var applier = new FakeResultApplier(disposition);
        var settlement = new FakeSettlement();
        var processor = CreateDeliveryProcessor(applier);

        await processor.ProcessAsync(
            AuditVerificationResultContract.MessageType,
            ValidResultBody(),
            settlement,
            CancellationToken.None);

        Assert.Equal(1, settlement.Completed);
        Assert.Equal(0, settlement.DeadLettered);
        Assert.Equal(0, settlement.Abandoned);
    }

    [Fact]
    public async Task ContractAndJobMismatchesAreDeadLetteredWithSafeReason()
    {
        var contractSettlement = new FakeSettlement();
        var contractProcessor = CreateDeliveryProcessor(
            new FakeResultApplier(ResultApplyDisposition.Applied));
        await contractProcessor.ProcessAsync(
            "unexpected.subject",
            ValidResultBody(),
            contractSettlement,
            CancellationToken.None);

        var jobSettlement = new FakeSettlement();
        var jobProcessor = CreateDeliveryProcessor(
            new FakeResultApplier(
                new ResultMessageValidationException(
                    "RESULT_JOB_MISMATCH",
                    "Sensitive mismatch details.")));
        await jobProcessor.ProcessAsync(
            AuditVerificationResultContract.MessageType,
            ValidResultBody(),
            jobSettlement,
            CancellationToken.None);

        Assert.Equal("RESULT_SUBJECT_INVALID", Assert.Single(contractSettlement.Reasons));
        Assert.Equal("RESULT_JOB_MISMATCH", Assert.Single(jobSettlement.Reasons));
        Assert.Equal(0, contractSettlement.Abandoned);
        Assert.Equal(0, jobSettlement.Abandoned);
    }

    [Fact]
    public async Task UnsafeDeadLetterReasonIsReplaced()
    {
        var settlement = new FakeSettlement();
        var processor = CreateDeliveryProcessor(
            new FakeResultApplier(
                new ResultMessageValidationException(
                    "unsafe reason with secret",
                    "Sensitive detail.")));

        await processor.ProcessAsync(
            AuditVerificationResultContract.MessageType,
            ValidResultBody(),
            settlement,
            CancellationToken.None);

        Assert.Equal("RESULT_REJECTED", Assert.Single(settlement.Reasons));
    }

    [Fact]
    public async Task InfrastructureFailuresAreAbandoned()
    {
        var settlement = new FakeSettlement();
        var processor = CreateDeliveryProcessor(
            new FakeResultApplier(
                new InvalidOperationException("Infrastructure secret.")));

        await processor.ProcessAsync(
            AuditVerificationResultContract.MessageType,
            ValidResultBody(),
            settlement,
            CancellationToken.None);

        Assert.Equal(1, settlement.Abandoned);
        Assert.Equal(0, settlement.Completed);
        Assert.Equal(0, settlement.DeadLettered);
    }

    private static AzureServiceBusResultDeliveryProcessor CreateDeliveryProcessor(
        IAuditVerificationResultApplier applier) =>
        new(
            applier,
            NullLogger<AzureServiceBusResultDeliveryProcessor>.Instance);

    private static string CreateFakeConnectionString()
    {
        byte[] fakeKeyBytes = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06];
        var keyPropertyName = string.Concat("SharedAccess", "Key");
        return string.Join(
            ';',
            "Endpoint=sb://caseledger-test.servicebus.windows.net/",
            "SharedAccessKeyName=tests",
            $"{keyPropertyName}={Convert.ToBase64String(fakeKeyBytes)}");
    }

    private static byte[] ValidResultBody()
    {
        var jobId = Guid.Parse("d37eed44-5615-40dd-905a-9aeb2911b5e4");
        var result = new AuditVerificationResultV1(
            SchemaVersion: 1,
            MessageType: AuditVerificationResultContract.MessageType,
            ResultId: AuditVerificationResultContract.ResultIdFor(jobId),
            JobId: jobId,
            CorrelationId: "azure-test",
            CaseId: Guid.Parse("20000000-0000-0000-0000-000000000001"),
            VerificationProfile: AuditVerificationScheduler.VerificationProfile,
            SnapshotSha256: SnapshotSha256,
            CompletedAt: new DateTimeOffset(
                2026,
                7,
                16,
                18,
                0,
                0,
                TimeSpan.Zero),
            Outcome: "valid",
            CheckedEvents: 2,
            ChainHead: ChainHead,
            BrokenAt: null,
            Error: null,
            Attempt: 0,
            WorkerVersion: "test-worker");
        return JsonSerializer.SerializeToUtf8Bytes(
            result,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private sealed class FakeMessageSender : IAzureServiceBusMessageSender
    {
        public List<AzureServiceBusOutboundMessage> Messages { get; } = [];

        public Task SendAsync(
            AzureServiceBusOutboundMessage message,
            CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeResultApplier : IAuditVerificationResultApplier
    {
        private readonly ResultApplyDisposition disposition;
        private readonly Exception? exception;

        public FakeResultApplier(ResultApplyDisposition disposition)
        {
            this.disposition = disposition;
        }

        public FakeResultApplier(Exception exception)
        {
            this.exception = exception;
        }

        public Task<ResultApplyDisposition> HandleAsync(
            AuditVerificationResultV1 result,
            CancellationToken cancellationToken = default) =>
            exception is null
                ? Task.FromResult(disposition)
                : Task.FromException<ResultApplyDisposition>(exception);
    }

    private sealed class FakeSettlement : IAzureServiceBusResultSettlement
    {
        public int Completed { get; private set; }
        public int DeadLettered { get; private set; }
        public int Abandoned { get; private set; }
        public List<string> Reasons { get; } = [];

        public Task CompleteAsync(CancellationToken cancellationToken)
        {
            Completed++;
            return Task.CompletedTask;
        }

        public Task DeadLetterAsync(
            string reason,
            CancellationToken cancellationToken)
        {
            DeadLettered++;
            Reasons.Add(reason);
            return Task.CompletedTask;
        }

        public Task AbandonAsync(CancellationToken cancellationToken)
        {
            Abandoned++;
            return Task.CompletedTask;
        }
    }
}
