using System.Text;
using CaseLedger.Api.Data;
using CaseLedger.Api.Domain;
using CaseLedger.Api.Messaging;
using CaseLedger.Api.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaseLedger.Api.Tests;

public sealed class MessagingRuntimeTests
{
    private static readonly Guid CaseId =
        Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now =
        new(2026, 7, 16, 18, 0, 0, TimeSpan.Zero);
    private const string SnapshotSha256 =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ChainHead =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task OutboxPublishSuccessMarksMessagePublished()
    {
        using var factory = new CaseLedgerFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var message = await AddOutboxAsync(db);
        var transport = new FakeOutboxTransport();
        var clock = new TestTimeProvider(Now);
        var processor = CreateProcessor(db, transport, clock);

        var dispatched = await processor.DispatchBatchAsync();

        Assert.Equal(1, dispatched);
        Assert.Single(transport.Published);
        var stored = await db.OutboxMessages.AsNoTracking()
            .SingleAsync(item => item.Id == message.Id);
        Assert.Equal(Now.UtcDateTime, stored.PublishedAt);
        Assert.Equal(1, stored.AttemptCount);
        Assert.Null(stored.LockId);
        Assert.Null(stored.LastErrorCode);
    }

    [Fact]
    public async Task OutboxFailureSchedulesExponentialRetryThenPublishes()
    {
        using var factory = new CaseLedgerFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var message = await AddOutboxAsync(db);
        var transport = new FakeOutboxTransport(
            new OutboxTransportException(
                "BROKER_UNAVAILABLE",
                "The fake broker is unavailable."),
            null);
        var clock = new TestTimeProvider(Now);
        var processor = CreateProcessor(db, transport, clock);

        Assert.Equal(1, await processor.DispatchBatchAsync());
        var retrying = await db.OutboxMessages.AsNoTracking()
            .SingleAsync(item => item.Id == message.Id);
        Assert.Equal(1, retrying.AttemptCount);
        Assert.Equal(Now.UtcDateTime.AddSeconds(2), retrying.NextAttemptAt);
        Assert.Equal("BROKER_UNAVAILABLE", retrying.LastErrorCode);
        Assert.Null(retrying.PublishedAt);
        Assert.Null(retrying.DeadLetteredAt);

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, await processor.DispatchBatchAsync());
        var published = await db.OutboxMessages.AsNoTracking()
            .SingleAsync(item => item.Id == message.Id);
        Assert.Equal(2, published.AttemptCount);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, published.PublishedAt);
        Assert.Null(published.LastErrorCode);
    }

    [Fact]
    public async Task OutboxFailureDeadLettersAtMaximumAttempts()
    {
        using var factory = new CaseLedgerFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var message = await AddOutboxAsync(db, attemptCount: 1);
        var transport = new FakeOutboxTransport(
            new InvalidOperationException("Sensitive fake detail."));
        var clock = new TestTimeProvider(Now);
        var processor = CreateProcessor(db, transport, clock, maxAttempts: 2);

        Assert.Equal(1, await processor.DispatchBatchAsync());

        var stored = await db.OutboxMessages.AsNoTracking()
            .SingleAsync(item => item.Id == message.Id);
        Assert.Equal(2, stored.AttemptCount);
        Assert.Equal(Now.UtcDateTime, stored.DeadLetteredAt);
        Assert.Equal("PUBLISH_FAILED", stored.LastErrorCode);
        Assert.Null(stored.PublishedAt);
        Assert.Null(stored.LockId);
    }

    [Fact]
    public async Task AttemptZeroValidResultCompletesJob()
    {
        using var factory = new CaseLedgerFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var job = await AddJobAsync(db);
        var notifier = new FakeVerificationNotifier();
        var handler = new AuditVerificationResultHandler(db, notifier);
        var result = ResultFor(job, "valid", checkedEvents: 2, chainHead: ChainHead);

        var disposition = await handler.HandleAsync(result);

        Assert.Equal(ResultApplyDisposition.Applied, disposition);
        var stored = await db.AuditVerificationJobs.AsNoTracking()
            .SingleAsync(item => item.Id == job.Id);
        Assert.Equal(AuditVerificationJobStatus.Completed, stored.Status);
        Assert.True(stored.Valid);
        Assert.Equal(2, stored.CheckedEvents);
        Assert.Equal(ChainHead, stored.ChainHead);
        Assert.Null(stored.ErrorCode);
        Assert.Single(notifier.Updates);
    }

    [Fact]
    public async Task InvalidResultWithErrorCompletesFalseAndStoresOnlyCode()
    {
        using var factory = new CaseLedgerFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var job = await AddJobAsync(db);
        var notifier = new FakeVerificationNotifier();
        var handler = new AuditVerificationResultHandler(db, notifier);
        var result = ResultFor(
            job,
            "invalid",
            checkedEvents: 1,
            brokenAt: 2,
            error: new AuditVerificationResultErrorV1(
                "HASH_MISMATCH",
                "This detailed verifier message must not be persisted.",
                Retryable: false));

        var disposition = await handler.HandleAsync(result);

        Assert.Equal(ResultApplyDisposition.Applied, disposition);
        var stored = await db.AuditVerificationJobs.AsNoTracking()
            .SingleAsync(item => item.Id == job.Id);
        Assert.Equal(AuditVerificationJobStatus.Completed, stored.Status);
        Assert.False(stored.Valid);
        Assert.Equal(2, stored.BrokenAt);
        Assert.Equal("HASH_MISMATCH", stored.ErrorCode);
        Assert.DoesNotContain(
            "detailed verifier",
            stored.ErrorCode,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DuplicateSameResultIsNoOpAndRebroadcasts()
    {
        using var factory = new CaseLedgerFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var job = await AddJobAsync(db);
        var notifier = new FakeVerificationNotifier();
        var handler = new AuditVerificationResultHandler(db, notifier);
        var result = ResultFor(job, "valid", checkedEvents: 2, chainHead: ChainHead);

        Assert.Equal(
            ResultApplyDisposition.Applied,
            await handler.HandleAsync(result));
        Assert.Equal(
            ResultApplyDisposition.Duplicate,
            await handler.HandleAsync(result));

        Assert.Equal(2, notifier.Updates.Count);
        Assert.All(
            notifier.Updates,
            update => Assert.Equal(result.ResultId, update.ResultId));
    }

    [Fact]
    public async Task SameResultIdWithDifferentContentIsRejectedAsCollision()
    {
        using var factory = new CaseLedgerFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var job = await AddJobAsync(db);
        var notifier = new FakeVerificationNotifier();
        var handler = new AuditVerificationResultHandler(db, notifier);
        var result = ResultFor(job, "valid", checkedEvents: 2, chainHead: ChainHead);
        await handler.HandleAsync(result);

        var exception = await Assert.ThrowsAsync<ResultMessageValidationException>(
            () => handler.HandleAsync(result with { CheckedEvents = 1 }));

        Assert.Equal("RESULT_ID_COLLISION", exception.ErrorCode);
        Assert.Single(notifier.Updates);
    }

    [Fact]
    public async Task TerminalErrorDeadLettersJobAndSanitizesErrorCode()
    {
        using var factory = new CaseLedgerFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var job = await AddJobAsync(db);
        var notifier = new FakeVerificationNotifier();
        var handler = new AuditVerificationResultHandler(db, notifier);
        var result = ResultFor(
            job,
            "error",
            checkedEvents: 0,
            error: new AuditVerificationResultErrorV1(
                "unsafe result code",
                "Retry budget exhausted.",
                Retryable: false));

        await handler.HandleAsync(result);

        var stored = await db.AuditVerificationJobs.AsNoTracking()
            .SingleAsync(item => item.Id == job.Id);
        Assert.Equal(AuditVerificationJobStatus.DeadLettered, stored.Status);
        Assert.Null(stored.Valid);
        Assert.Equal("WORKER_ERROR", stored.ErrorCode);
    }

    [Fact]
    public void InvalidJsonUsesGenericSafeFailureMessage()
    {
        var exception = Assert.Throws<ResultMessageValidationException>(
            () => AuditVerificationResultContract.Parse(
                Encoding.UTF8.GetBytes("{\"schemaVersion\":")));

        Assert.Equal("INVALID_RESULT_JSON", exception.ErrorCode);
        Assert.Equal(
            "The verification result is not valid contract JSON.",
            exception.Message);
    }

    [Fact]
    public void OutcomeSpecificContractInvariantsRejectAmbiguousResults()
    {
        var job = new AuditVerificationJob
        {
            Id = Guid.NewGuid(),
            CaseId = CaseId,
            SnapshotSha256 = SnapshotSha256
        };
        var terminalError = new AuditVerificationResultErrorV1(
            "HASH_MISMATCH",
            "Public verifier error.",
            Retryable: false);
        var valid = ResultFor(
            job,
            "valid",
            checkedEvents: 2,
            chainHead: ChainHead);
        var invalid = ResultFor(
            job,
            "invalid",
            checkedEvents: 1,
            brokenAt: 2,
            error: terminalError);
        var error = ResultFor(
            job,
            "error",
            checkedEvents: 0,
            error: terminalError);
        AuditVerificationResultV1[] ambiguous =
        [
            valid with { ChainHead = null },
            valid with { Error = terminalError },
            invalid with { Error = null },
            invalid with { ChainHead = ChainHead },
            error with { Error = null },
            error with { ChainHead = ChainHead }
        ];

        foreach (var result in ambiguous)
        {
            var exception = Assert.Throws<ResultMessageValidationException>(
                () => AuditVerificationResultContract.Validate(result));
            Assert.Equal("RESULT_SCHEMA_INVALID", exception.ErrorCode);
        }
    }

    private static OutboxDispatchProcessor CreateProcessor(
        CaseLedgerDbContext db,
        IOutboxTransport transport,
        TimeProvider clock,
        int maxAttempts = 4) =>
        new(
            db,
            transport,
            Options.Create(new MessagingOptions
            {
                BatchSize = 10,
                LeaseSeconds = 30,
                MaxAttempts = maxAttempts
            }),
            clock,
            NullLogger<OutboxDispatchProcessor>.Instance);

    private static async Task<OutboxMessage> AddOutboxAsync(
        CaseLedgerDbContext db,
        int attemptCount = 0)
    {
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            MessageType = AuditVerificationScheduler.RequestMessageType,
            PayloadJson = "{\"schemaVersion\":1}",
            OccurredAt = Now.UtcDateTime,
            NextAttemptAt = Now.UtcDateTime,
            AttemptCount = attemptCount
        };
        db.OutboxMessages.Add(message);
        await db.SaveChangesAsync();
        return message;
    }

    private static async Task<AuditVerificationJob> AddJobAsync(
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

    private static AuditVerificationResultV1 ResultFor(
        AuditVerificationJob job,
        string outcome,
        int checkedEvents,
        string? chainHead = null,
        int? brokenAt = null,
        AuditVerificationResultErrorV1? error = null) =>
        new(
            SchemaVersion: 1,
            MessageType: AuditVerificationResultContract.MessageType,
            ResultId: AuditVerificationResultContract.ResultIdFor(job.Id),
            JobId: job.Id,
            CorrelationId: "runtime-test",
            CaseId: job.CaseId,
            VerificationProfile: AuditVerificationScheduler.VerificationProfile,
            SnapshotSha256: job.SnapshotSha256,
            CompletedAt: Now,
            Outcome: outcome,
            CheckedEvents: checkedEvents,
            ChainHead: chainHead,
            BrokenAt: brokenAt,
            Error: error,
            Attempt: 0,
            WorkerVersion: "test-worker");

    private sealed class FakeOutboxTransport(params Exception?[] outcomes)
        : IOutboxTransport
    {
        private readonly Queue<Exception?> outcomes = new(outcomes);

        public List<OutboxDispatchMessage> Published { get; } = [];

        public Task PublishAsync(
            OutboxDispatchMessage message,
            CancellationToken cancellationToken)
        {
            Published.Add(message);
            if (outcomes.Count > 0 && outcomes.Dequeue() is { } failure)
            {
                throw failure;
            }

            return Task.CompletedTask;
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
}
