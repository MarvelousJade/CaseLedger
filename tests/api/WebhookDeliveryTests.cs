using System.Net;
using System.Text.Json;
using CaseLedger.Api.Data;
using CaseLedger.Api.Domain;
using CaseLedger.Api.Messaging;
using CaseLedger.Api.Realtime;
using CaseLedger.Api.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaseLedger.Api.Tests;

public sealed class WebhookDeliveryTests
{
    private static readonly Guid CaseId =
        Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now =
        new(2026, 7, 16, 18, 0, 0, TimeSpan.Zero);
    private const string SigningSecret =
        "0123456789abcdef0123456789abcdef";
    private const string SnapshotSha256 =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ChainHead =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task FirstResultStagesPrivacyMinimalDeliveryAndDuplicateIsNoOp()
    {
        using var factory = new CaseLedgerFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var job = await AddQueuedJobAsync(db);
        var notifier = new FakeVerificationNotifier();
        var handler = CreateResultHandler(db, notifier);
        var result = ValidResult(job);

        Assert.Equal(
            ResultApplyDisposition.Applied,
            await handler.HandleAsync(result));

        var delivery = await db.WebhookDeliveries
            .AsNoTracking()
            .SingleAsync(item => item.VerificationJobId == job.Id);
        Assert.Equal(result.ResultId, delivery.ResultId);
        Assert.Equal(VerificationWebhookV1.EventType, delivery.EventType);
        Assert.Equal(Now.UtcDateTime, delivery.CreatedAt);
        Assert.Equal(Now.UtcDateTime, delivery.NextAttemptAt);
        Assert.Null(delivery.DeliveredAt);
        Assert.Null(delivery.DeadLetteredAt);

        using var payload = JsonDocument.Parse(delivery.PayloadJson);
        var root = payload.RootElement;
        Assert.Equal(
            [
                "deliveryId",
                "resultId",
                "jobId",
                "caseId",
                "status",
                "valid",
                "checkedEvents",
                "brokenAt",
                "chainHead",
                "errorCode",
                "completedAt"
            ],
            root.EnumerateObject().Select(item => item.Name).ToArray());
        Assert.Equal(delivery.Id, root.GetProperty("deliveryId").GetGuid());
        Assert.Equal(result.ResultId, root.GetProperty("resultId").GetString());
        Assert.Equal(job.Id, root.GetProperty("jobId").GetGuid());
        Assert.Equal(job.CaseId, root.GetProperty("caseId").GetGuid());
        Assert.Equal("Completed", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("valid").GetBoolean());
        Assert.Equal(2, root.GetProperty("checkedEvents").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("brokenAt").ValueKind);
        Assert.Equal(ChainHead, root.GetProperty("chainHead").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("errorCode").ValueKind);
        Assert.Equal(
            "2026-07-16T18:00:00.0000000Z",
            root.GetProperty("completedAt").GetString());
        Assert.DoesNotContain("name", delivery.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "description",
            delivery.PayloadJson,
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(
            ResultApplyDisposition.Duplicate,
            await handler.HandleAsync(result));
        Assert.Equal(
            1,
            await db.WebhookDeliveries.CountAsync(
                item => item.VerificationJobId == job.Id));
        Assert.Equal(2, notifier.Updates.Count);
    }

    [Fact]
    public async Task DeliveryConstraintFailureRollsBackResultApplication()
    {
        using var factory = new CaseLedgerFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var job = await AddQueuedJobAsync(db);
        var otherJob = await AddQueuedJobAsync(db);
        var result = ValidResult(job);
        db.WebhookDeliveries.Add(new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            VerificationJobId = otherJob.Id,
            ResultId = result.ResultId,
            EventType = VerificationWebhookV1.EventType,
            PayloadJson = "{}",
            CreatedAt = Now.UtcDateTime.AddMinutes(-1),
            NextAttemptAt = Now.UtcDateTime.AddMinutes(-1)
        });
        await db.SaveChangesAsync();
        var handler = CreateResultHandler(db, new FakeVerificationNotifier());

        await Assert.ThrowsAsync<DbUpdateException>(
            () => handler.HandleAsync(result));

        db.ChangeTracker.Clear();
        var stored = await db.AuditVerificationJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == job.Id);
        Assert.Equal(AuditVerificationJobStatus.Queued, stored.Status);
        Assert.Null(stored.ResultId);
        Assert.Null(stored.CompletedAt);
        Assert.Equal(
            1,
            await db.WebhookDeliveries.CountAsync(
                item => item.ResultId == result.ResultId));
    }

    [Fact]
    public void SignatureMatchesFixedUtf8HmacVector()
    {
        const string timestamp = "1784224800";
        const string body =
            "{\"status\":\"Completed\",\"note\":\"Montréal 雪\"}";

        Assert.Equal(
            "v1=debd4d1a6770a6c51ada45a3e8ef721fc28645b7286ff6bc1c7a6b0774afce1b",
            WebhookSignature.Create(SigningSecret, timestamp, body));
    }

    [Fact]
    public async Task HttpTransportPostsExactBodyAndRequiredHeaders()
    {
        var handler = new RecordingHttpMessageHandler();
        using var client = new HttpClient(handler);
        var transport = new HttpWebhookTransport(
            new StaticHttpClientFactory(client),
            Options.Create(ValidOptions()));
        var deliveryId =
            Guid.Parse("d37eed44-5615-40dd-905a-9aeb2911b5e4");
        const string body = "{\"status\":\"Completed\",\"valid\":true}";
        var request = new WebhookDispatchRequest(
            deliveryId,
            VerificationWebhookV1.EventType,
            body,
            "1784224800",
            "v1=abc123");

        var response = await transport.PostAsync(
            request,
            CancellationToken.None);

        Assert.Equal(204, response.StatusCode);
        Assert.Equal(body, handler.Body);
        Assert.Equal("application/json; charset=utf-8", handler.ContentType);
        Assert.Equal(deliveryId.ToString("D"), handler.Header("X-CaseLedger-Delivery"));
        Assert.Equal(VerificationWebhookV1.EventType, handler.Header("X-CaseLedger-Event"));
        Assert.Equal("1784224800", handler.Header("X-CaseLedger-Timestamp"));
        Assert.Equal("v1=abc123", handler.Header("X-CaseLedger-Signature"));
        Assert.Equal(deliveryId.ToString("D"), handler.Header("Idempotency-Key"));
    }

    [Fact]
    public async Task SuccessfulDispatchMarksDeliveryDelivered()
    {
        using var factory = new CaseLedgerFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var delivery = await AddDeliveryAsync(db);
        var transport = new FakeWebhookTransport(
            new WebhookTransportResponse(204));
        var clock = new TestTimeProvider(Now);
        var processor = CreateProcessor(db, transport, clock);

        Assert.Equal(1, await processor.DispatchBatchAsync());

        var request = Assert.Single(transport.Requests);
        Assert.Equal(delivery.PayloadJson, request.PayloadJson);
        Assert.Equal("1784224800", request.Timestamp);
        Assert.Equal(
            WebhookSignature.Create(
                SigningSecret,
                request.Timestamp,
                delivery.PayloadJson),
            request.Signature);
        var stored = await db.WebhookDeliveries.AsNoTracking()
            .SingleAsync(item => item.Id == delivery.Id);
        Assert.Equal(Now.UtcDateTime, stored.DeliveredAt);
        Assert.Equal(1, stored.AttemptCount);
        Assert.Null(stored.LastErrorCode);
        Assert.Null(stored.LockId);
    }

    [Fact]
    public async Task RetryableResponseSchedulesBackoffThenSucceeds()
    {
        using var factory = new CaseLedgerFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var delivery = await AddDeliveryAsync(db);
        var transport = new FakeWebhookTransport(
            new WebhookTransportResponse(429),
            new WebhookTransportResponse(200));
        var clock = new TestTimeProvider(Now);
        var processor = CreateProcessor(db, transport, clock);

        Assert.Equal(1, await processor.DispatchBatchAsync());
        var retrying = await db.WebhookDeliveries.AsNoTracking()
            .SingleAsync(item => item.Id == delivery.Id);
        Assert.Equal(1, retrying.AttemptCount);
        Assert.Equal(Now.UtcDateTime.AddSeconds(2), retrying.NextAttemptAt);
        Assert.Equal("WEBHOOK_HTTP_429", retrying.LastErrorCode);
        Assert.Null(retrying.DeadLetteredAt);

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, await processor.DispatchBatchAsync());
        var delivered = await db.WebhookDeliveries.AsNoTracking()
            .SingleAsync(item => item.Id == delivery.Id);
        Assert.Equal(2, delivered.AttemptCount);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, delivered.DeliveredAt);
        Assert.Null(delivered.LastErrorCode);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task RetryableHttpResponsesRemainPending(int statusCode)
    {
        using var factory = new CaseLedgerFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var delivery = await AddDeliveryAsync(db);
        var transport = new FakeWebhookTransport(
            new WebhookTransportResponse(statusCode));
        var clock = new TestTimeProvider(Now);
        var processor = CreateProcessor(db, transport, clock);

        Assert.Equal(1, await processor.DispatchBatchAsync());

        var stored = await db.WebhookDeliveries.AsNoTracking()
            .SingleAsync(item => item.Id == delivery.Id);
        Assert.Equal(1, stored.AttemptCount);
        Assert.Equal(Now.UtcDateTime.AddSeconds(2), stored.NextAttemptAt);
        Assert.Equal($"WEBHOOK_HTTP_{statusCode}", stored.LastErrorCode);
        Assert.Null(stored.DeliveredAt);
        Assert.Null(stored.DeadLetteredAt);
    }

    [Fact]
    public async Task NetworkFailureRemainsPendingBeforeMaximumAttempts()
    {
        using var factory = new CaseLedgerFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var delivery = await AddDeliveryAsync(db);
        var transport = new FakeWebhookTransport(
            new WebhookTransportException(
                "WEBHOOK_NETWORK_ERROR",
                "Sensitive network detail."));
        var clock = new TestTimeProvider(Now);
        var processor = CreateProcessor(db, transport, clock);

        Assert.Equal(1, await processor.DispatchBatchAsync());

        var stored = await db.WebhookDeliveries.AsNoTracking()
            .SingleAsync(item => item.Id == delivery.Id);
        Assert.Equal(Now.UtcDateTime.AddSeconds(2), stored.NextAttemptAt);
        Assert.Equal("WEBHOOK_NETWORK_ERROR", stored.LastErrorCode);
        Assert.Null(stored.DeadLetteredAt);
    }

    [Fact]
    public async Task ExpiredLeaseIsReclaimedAfterActiveLeaseIsSkipped()
    {
        using var factory = new CaseLedgerFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var delivery = await AddDeliveryAsync(db);
        var activeLock = Guid.NewGuid();
        await db.WebhookDeliveries
            .Where(item => item.Id == delivery.Id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.LockId, (Guid?)activeLock)
                    .SetProperty(
                        item => item.LockedUntil,
                        (DateTime?)Now.UtcDateTime.AddSeconds(30)));
        var transport = new FakeWebhookTransport(
            new WebhookTransportResponse(204));
        var clock = new TestTimeProvider(Now);
        var processor = CreateProcessor(db, transport, clock);

        Assert.Equal(0, await processor.DispatchBatchAsync());
        Assert.Empty(transport.Requests);

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, await processor.DispatchBatchAsync());
        var stored = await db.WebhookDeliveries.AsNoTracking()
            .SingleAsync(item => item.Id == delivery.Id);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, stored.DeliveredAt);
        Assert.Null(stored.LockId);
    }

    [Fact]
    public async Task PermanentClientResponseDeadLettersImmediately()
    {
        using var factory = new CaseLedgerFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var delivery = await AddDeliveryAsync(db);
        var transport = new FakeWebhookTransport(
            new WebhookTransportResponse(400));
        var clock = new TestTimeProvider(Now);
        var processor = CreateProcessor(db, transport, clock);

        Assert.Equal(1, await processor.DispatchBatchAsync());

        var stored = await db.WebhookDeliveries.AsNoTracking()
            .SingleAsync(item => item.Id == delivery.Id);
        Assert.Equal(1, stored.AttemptCount);
        Assert.Equal(Now.UtcDateTime, stored.DeadLetteredAt);
        Assert.Equal("WEBHOOK_HTTP_400", stored.LastErrorCode);
        Assert.Null(stored.DeliveredAt);
    }

    [Fact]
    public async Task NetworkFailureDeadLettersAtMaximumAttempts()
    {
        using var factory = new CaseLedgerFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var delivery = await AddDeliveryAsync(db, attemptCount: 1);
        var transport = new FakeWebhookTransport(
            new WebhookTransportException(
                "WEBHOOK_NETWORK_ERROR",
                "Sensitive network detail."));
        var clock = new TestTimeProvider(Now);
        var processor = CreateProcessor(
            db,
            transport,
            clock,
            maxAttempts: 2);

        Assert.Equal(1, await processor.DispatchBatchAsync());

        var stored = await db.WebhookDeliveries.AsNoTracking()
            .SingleAsync(item => item.Id == delivery.Id);
        Assert.Equal(2, stored.AttemptCount);
        Assert.Equal(Now.UtcDateTime, stored.DeadLetteredAt);
        Assert.Equal("WEBHOOK_NETWORK_ERROR", stored.LastErrorCode);
    }

    [Fact]
    public void OptionsRequireExplicitHttpOptInWithoutEchoingSecrets()
    {
        const string secret = "do-not-echo-this-webhook-secret!";
        const string destination =
            "http://webhook-receiver:8082/caseledger";
        var exception = Assert.Throws<InvalidOperationException>(
            () => WebhookRuntimeOptions.Validate(
                ValidOptions() with
                {
                    DestinationUrl = destination,
                    SigningSecret = secret,
                    AllowInsecureHttp = false
                }));

        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(destination, exception.ToString(), StringComparison.Ordinal);
        var allowed = WebhookRuntimeOptions.Validate(
            ValidOptions() with
            {
                DestinationUrl = destination,
                AllowInsecureHttp = true
            });
        Assert.Equal(destination, allowed.AbsoluteUri.TrimEnd('/'));
    }

    private static AuditVerificationResultHandler CreateResultHandler(
        CaseLedgerDbContext db,
        IVerificationUpdateNotifier notifier) =>
        new(
            db,
            notifier,
            Options.Create(ValidOptions() with { Enabled = true }),
            new TestTimeProvider(Now));

    private static WebhookDispatchProcessor CreateProcessor(
        CaseLedgerDbContext db,
        IWebhookTransport transport,
        TimeProvider clock,
        int maxAttempts = 4) =>
        new(
            db,
            transport,
            Options.Create(ValidOptions() with { MaxAttempts = maxAttempts }),
            clock,
            NullLogger<WebhookDispatchProcessor>.Instance);

    private static WebhookOptions ValidOptions() =>
        new()
        {
            Enabled = true,
            DestinationUrl = "https://webhook.example.test/caseledger",
            SigningSecret = SigningSecret,
            PollingIntervalMilliseconds = 1_000,
            BatchSize = 10,
            LeaseSeconds = 30,
            MaxAttempts = 4,
            TimeoutSeconds = 10
        };

    private static async Task<AuditVerificationJob> AddQueuedJobAsync(
        CaseLedgerDbContext db)
    {
        var job = new AuditVerificationJob
        {
            Id = Guid.NewGuid(),
            CaseId = CaseId,
            Status = AuditVerificationJobStatus.Queued,
            TargetSequence = 2,
            TargetHash = ChainHead,
            SnapshotSha256 = SnapshotSha256,
            RequestedAt = Now.UtcDateTime.AddMinutes(-1)
        };
        db.AuditVerificationJobs.Add(job);
        await db.SaveChangesAsync();
        return job;
    }

    private static AuditVerificationResultV1 ValidResult(
        AuditVerificationJob job) =>
        new(
            SchemaVersion: 1,
            MessageType: AuditVerificationResultContract.MessageType,
            ResultId: AuditVerificationResultContract.ResultIdFor(job.Id),
            JobId: job.Id,
            CorrelationId: "webhook-test",
            CaseId: job.CaseId,
            VerificationProfile: AuditVerificationScheduler.VerificationProfile,
            SnapshotSha256: job.SnapshotSha256,
            CompletedAt: Now,
            Outcome: "valid",
            CheckedEvents: 2,
            ChainHead: ChainHead,
            BrokenAt: null,
            Error: null,
            Attempt: 0,
            WorkerVersion: "test-worker");

    private static async Task<WebhookDelivery> AddDeliveryAsync(
        CaseLedgerDbContext db,
        int attemptCount = 0)
    {
        var job = await AddQueuedJobAsync(db);
        job.Status = AuditVerificationJobStatus.Completed;
        job.ResultId = AuditVerificationResultContract.ResultIdFor(job.Id);
        job.Valid = true;
        job.CheckedEvents = 2;
        job.ChainHead = ChainHead;
        job.CompletedAt = Now.UtcDateTime;
        var delivery = new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            VerificationJobId = job.Id,
            VerificationJob = job,
            ResultId = job.ResultId!,
            EventType = VerificationWebhookV1.EventType,
            PayloadJson = "{\"status\":\"Completed\",\"note\":\"Montréal 雪\"}",
            CreatedAt = Now.UtcDateTime,
            NextAttemptAt = Now.UtcDateTime,
            AttemptCount = attemptCount
        };
        db.WebhookDeliveries.Add(delivery);
        await db.SaveChangesAsync();
        return delivery;
    }

    private sealed class FakeWebhookTransport(params object[] outcomes)
        : IWebhookTransport
    {
        private readonly Queue<object> outcomes = new(outcomes);

        public List<WebhookDispatchRequest> Requests { get; } = [];

        public Task<WebhookTransportResponse> PostAsync(
            WebhookDispatchRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (outcomes.Count == 0)
            {
                return Task.FromResult(new WebhookTransportResponse(204));
            }

            return outcomes.Dequeue() switch
            {
                WebhookTransportResponse response => Task.FromResult(response),
                Exception exception =>
                    Task.FromException<WebhookTransportResponse>(exception),
                _ => throw new InvalidOperationException("Unsupported fake outcome.")
            };
        }
    }

    private sealed class FakeVerificationNotifier : IVerificationUpdateNotifier
    {
        public List<VerificationUpdatedDto> Updates { get; } = [];

        public Task NotifyAsync(
            VerificationUpdatedDto update,
            CancellationToken cancellationToken)
        {
            Updates.Add(update);
            return Task.CompletedTask;
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset now = now;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class StaticHttpClientFactory(HttpClient client)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> headers =
            new(StringComparer.OrdinalIgnoreCase);

        public string? Body { get; private set; }
        public string? ContentType { get; private set; }

        public string? Header(string name) =>
            headers.GetValueOrDefault(name);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            foreach (var header in request.Headers)
            {
                headers[header.Key] = Assert.Single(header.Value);
            }

            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            ContentType = request.Content.Headers.ContentType?.ToString();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
