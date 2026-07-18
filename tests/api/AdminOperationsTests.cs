using System.Net;
using System.Net.Http.Json;
using CaseLedger.Api.Contracts;
using CaseLedger.Api.Data;
using CaseLedger.Api.Domain;
using CaseLedger.Api.Messaging;
using CaseLedger.Api.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaseLedger.Api.Tests;

public sealed class AdminOperationsTests
{
    private static readonly DateTime RequestDeadLetteredAt =
        new(2026, 7, 18, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WebhookDeadLetteredAt =
        new(2026, 7, 18, 3, 5, 0, DateTimeKind.Utc);

    [Fact]
    public async Task AnalystCannotInspectOrReplayOperationalFailures()
    {
        using var factory = EnabledFactory();
        using var client = factory.CreateCookieClient();
        await client.LoginAsync();

        var list = await client.GetAsync("/api/admin/operations/failures");
        var replay = await client.PostAsJsonAsync(
            $"/api/admin/operations/verification-requests/{Guid.NewGuid():D}/replay",
            new OperationalReplayRequest(RequestDeadLetteredAt, "Broker recovered"));

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
    }

    [Fact]
    public async Task FailureListIsRedactedAndTerminalVerificationJobsAreReadOnly()
    {
        using var factory = EnabledFactory();
        var request = await AddDeadVerificationRequestAsync(factory);
        var webhook = await AddDeadWebhookAsync(factory);
        var terminalJobId = await AddTerminalVerificationJobAsync(factory);
        using var client = factory.CreateCookieClient();
        await client.LoginAsync("admin@caseledger.dev", "Admin123!");

        var response = await client.GetAsync("/api/admin/operations/failures?page=1&pageSize=10");

        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync();
        var failures = await response.Content
            .ReadRequiredJsonAsync<OperationalFailureCollectionResponse>();
        Assert.Equal(3, failures.Total);
        Assert.Equal(3, failures.Items.Count);
        Assert.DoesNotContain(request.PayloadJson, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(webhook.PayloadJson, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("payloadJson", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lockId", raw, StringComparison.OrdinalIgnoreCase);

        var requestFailure = Assert.Single(
            failures.Items,
            item => item.Kind == "verification-request" && item.Id == request.Id);
        Assert.True(requestFailure.Replayable);
        Assert.Equal(request.AttemptCount, requestFailure.AttemptCount);
        Assert.Equal(request.LastErrorCode, requestFailure.ErrorCode);

        var webhookFailure = Assert.Single(
            failures.Items,
            item => item.Kind == "webhook-delivery" && item.Id == webhook.Id);
        Assert.True(webhookFailure.Replayable);

        var terminalFailure = Assert.Single(
            failures.Items,
            item => item.Kind == "verification-job" && item.Id == terminalJobId);
        Assert.False(terminalFailure.Replayable);
        Assert.Contains("immutable", terminalFailure.ReplayBlockedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerificationRequestReplayPreservesIdentityAndPayloadAndWritesAuditOnce()
    {
        using var factory = EnabledFactory();
        var source = await AddDeadVerificationRequestAsync(factory);
        using var client = factory.CreateCookieClient();
        await client.LoginAsync("admin@caseledger.dev", "Admin123!");
        var command = new OperationalReplayRequest(
            source.DeadLetteredAt,
            "Service Bus connectivity restored");

        var accepted = await client.PostAsJsonAsync(
            $"/api/admin/operations/verification-requests/{source.Id:D}/replay",
            command);
        var repeated = await client.PostAsJsonAsync(
            $"/api/admin/operations/verification-requests/{source.Id:D}/replay",
            command);

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
        var replay = await accepted.Content.ReadRequiredJsonAsync<OperationalReplayResponse>();
        Assert.Equal("verification-request", replay.Kind);
        Assert.Equal(source.Id, replay.SourceId);
        Assert.Equal(source.DeadLetteredAt, replay.SourceDeadLetteredAt);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var stored = await db.OutboxMessages.AsNoTracking().SingleAsync(item => item.Id == source.Id);
        Assert.Equal(source.Id, stored.Id);
        Assert.Equal(source.PayloadJson, stored.PayloadJson);
        Assert.Null(stored.DeadLetteredAt);
        Assert.Null(stored.PublishedAt);
        Assert.Equal(0, stored.AttemptCount);
        Assert.Equal(replay.ReplayedAt, stored.NextAttemptAt);
        Assert.Null(stored.LockId);
        Assert.Null(stored.LockedUntil);
        Assert.Null(stored.LastErrorCode);

        var audit = Assert.Single(await db.OperationalReplays.AsNoTracking().ToListAsync());
        Assert.Equal(replay.ReplayId, audit.Id);
        Assert.Equal(OperationalReplayKind.VerificationRequest, audit.Kind);
        Assert.Equal(source.Id, audit.SourceId);
        Assert.Equal(source.DeadLetteredAt, audit.SourceDeadLetteredAt);
        Assert.Equal(DatabaseSeeder.AdminId, audit.ActorId);
        Assert.Equal(source.AttemptCount, audit.PreviousAttemptCount);
        Assert.Equal(source.LastErrorCode, audit.PreviousErrorCode);
        Assert.Equal(command.Reason, audit.Reason);
    }

    [Fact]
    public async Task VerificationRequestReplayRejectsStaleLeaseAndAnomalousState()
    {
        using var factory = EnabledFactory();
        var source = await AddDeadVerificationRequestAsync(factory);
        using var client = factory.CreateCookieClient();
        await client.LoginAsync("admin@caseledger.dev", "Admin123!");

        var stale = await client.PostAsJsonAsync(
            $"/api/admin/operations/verification-requests/{source.Id:D}/replay",
            new OperationalReplayRequest(
                source.DeadLetteredAt.AddSeconds(-1),
                "Stale operator view"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        await UpdateRequestAsync(factory, source.Id, item =>
        {
            item.LockId = Guid.NewGuid();
            item.LockedUntil = DateTime.UtcNow.AddMinutes(5);
        });
        var leased = await client.PostAsJsonAsync(
            $"/api/admin/operations/verification-requests/{source.Id:D}/replay",
            new OperationalReplayRequest(source.DeadLetteredAt, "Lease must be respected"));
        Assert.Equal(HttpStatusCode.Conflict, leased.StatusCode);

        await UpdateRequestAsync(factory, source.Id, item =>
        {
            item.LockId = null;
            item.LockedUntil = null;
            item.PublishedAt = source.DeadLetteredAt;
        });
        var anomalous = await client.PostAsJsonAsync(
            $"/api/admin/operations/verification-requests/{source.Id:D}/replay",
            new OperationalReplayRequest(source.DeadLetteredAt, "Conflicting state must fail"));
        Assert.Equal(HttpStatusCode.Conflict, anomalous.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        Assert.False(await db.OperationalReplays.AnyAsync());
    }

    [Fact]
    public async Task ConcurrentVerificationRequestReplayHasOneWinnerAndOneAuditRecord()
    {
        using var factory = EnabledFactory();
        var source = await AddDeadVerificationRequestAsync(factory);
        using var firstClient = factory.CreateCookieClient();
        using var secondClient = factory.CreateCookieClient();
        await firstClient.LoginAsync("admin@caseledger.dev", "Admin123!");
        await secondClient.LoginAsync("admin@caseledger.dev", "Admin123!");
        var command = new OperationalReplayRequest(source.DeadLetteredAt, "Single recovery action");

        var responses = await Task.WhenAll(
            firstClient.PostAsJsonAsync(
                $"/api/admin/operations/verification-requests/{source.Id:D}/replay",
                command),
            secondClient.PostAsJsonAsync(
                $"/api/admin/operations/verification-requests/{source.Id:D}/replay",
                command));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Accepted);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        Assert.Equal(1, await db.OperationalReplays.CountAsync());
    }

    [Fact]
    public async Task WebhookReplayPreservesIdentityAndPayloadAndWritesAudit()
    {
        using var factory = EnabledFactory();
        var source = await AddDeadWebhookAsync(factory);
        using var client = factory.CreateCookieClient();
        await client.LoginAsync("admin@caseledger.dev", "Admin123!");

        var accepted = await client.PostAsJsonAsync(
            $"/api/admin/operations/webhooks/{source.Id:D}/replay",
            new OperationalReplayRequest(source.DeadLetteredAt, "Receiver deployment repaired"));

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var replay = await accepted.Content.ReadRequiredJsonAsync<OperationalReplayResponse>();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var stored = await db.WebhookDeliveries.AsNoTracking()
            .SingleAsync(item => item.Id == source.Id);
        Assert.Equal(source.Id, stored.Id);
        Assert.Equal(source.PayloadJson, stored.PayloadJson);
        Assert.Null(stored.DeadLetteredAt);
        Assert.Null(stored.DeliveredAt);
        Assert.Equal(0, stored.AttemptCount);
        Assert.Equal(replay.ReplayedAt, stored.NextAttemptAt);
        Assert.Null(stored.LockId);
        Assert.Null(stored.LockedUntil);
        Assert.Null(stored.LastErrorCode);

        var audit = Assert.Single(await db.OperationalReplays.AsNoTracking().ToListAsync());
        Assert.Equal(OperationalReplayKind.WebhookDelivery, audit.Kind);
        Assert.Equal(source.Id, audit.SourceId);
        Assert.Equal(DatabaseSeeder.AdminId, audit.ActorId);
        Assert.Equal(source.AttemptCount, audit.PreviousAttemptCount);
        Assert.Equal(source.LastErrorCode, audit.PreviousErrorCode);
    }

    [Fact]
    public async Task ConcurrentWebhookReplayHasOneWinnerAndOneAuditRecord()
    {
        using var factory = EnabledFactory();
        var source = await AddDeadWebhookAsync(factory);
        using var firstClient = factory.CreateCookieClient();
        using var secondClient = factory.CreateCookieClient();
        await firstClient.LoginAsync("admin@caseledger.dev", "Admin123!");
        await secondClient.LoginAsync("admin@caseledger.dev", "Admin123!");
        var command = new OperationalReplayRequest(source.DeadLetteredAt, "Single webhook retry");

        var responses = await Task.WhenAll(
            firstClient.PostAsJsonAsync(
                $"/api/admin/operations/webhooks/{source.Id:D}/replay",
                command),
            secondClient.PostAsJsonAsync(
                $"/api/admin/operations/webhooks/{source.Id:D}/replay",
                command));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Accepted);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        Assert.Equal(1, await db.OperationalReplays.CountAsync());
    }

    [Fact]
    public async Task DisabledRuntimesRejectReplayWithoutMutation()
    {
        using var factory = new CaseLedgerFactory();
        var source = await AddDeadVerificationRequestAsync(factory, useScheduler: false);
        using var client = factory.CreateCookieClient();
        await client.LoginAsync("admin@caseledger.dev", "Admin123!");

        var response = await client.PostAsJsonAsync(
            $"/api/admin/operations/verification-requests/{source.Id:D}/replay",
            new OperationalReplayRequest(source.DeadLetteredAt, "Runtime disabled"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var stored = await db.OutboxMessages.AsNoTracking().SingleAsync(item => item.Id == source.Id);
        Assert.Equal(source.DeadLetteredAt, stored.DeadLetteredAt);
        Assert.False(await db.OperationalReplays.AnyAsync());
    }

    private static CaseLedgerFactory EnabledFactory() => new(
        messagingEnabled: true,
        configurationOverrides: new Dictionary<string, string?>
        {
            ["Webhook:Enabled"] = "true",
            ["Webhook:DestinationUrl"] = "https://webhook.example.invalid/caseledger",
            ["Webhook:SigningSecret"] = new string('s', 32)
        });

    private static async Task<SeededOutbox> AddDeadVerificationRequestAsync(
        CaseLedgerFactory factory,
        bool useScheduler = true)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var caseRecord = await db.Cases.SingleAsync(item => item.Reference == "CL-2026-001");
        AuditVerificationJob job;
        if (useScheduler)
        {
            var scheduler = scope.ServiceProvider.GetRequiredService<AuditVerificationScheduler>();
            job = await scheduler.ScheduleAsync(caseRecord.Id, "admin-operations-test");
        }
        else
        {
            job = NewQueuedJob(caseRecord);
            db.AuditVerificationJobs.Add(job);
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = job.Id,
                MessageType = AuditVerificationScheduler.RequestMessageType,
                PayloadJson = "{\"private\":\"verification-request-payload\"}",
                OccurredAt = RequestDeadLetteredAt.AddMinutes(-1),
                NextAttemptAt = RequestDeadLetteredAt.AddMinutes(-1)
            });
        }

        await db.SaveChangesAsync();
        var outbox = await db.OutboxMessages.SingleAsync(item => item.Id == job.Id);
        outbox.DeadLetteredAt = RequestDeadLetteredAt;
        outbox.AttemptCount = 8;
        outbox.LastErrorCode = "BROKER_UNAVAILABLE";
        await db.SaveChangesAsync();
        return new SeededOutbox(
            outbox.Id,
            outbox.PayloadJson,
            RequestDeadLetteredAt,
            outbox.AttemptCount,
            outbox.LastErrorCode!);
    }

    private static async Task<SeededWebhook> AddDeadWebhookAsync(CaseLedgerFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var caseRecord = await db.Cases.SingleAsync(item => item.Reference == "CL-2026-002");
        var job = NewCompletedJob(caseRecord);
        const string payload = "{\"private\":\"webhook-payload-must-not-leak\"}";
        var delivery = new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            VerificationJobId = job.Id,
            VerificationJob = job,
            ResultId = job.ResultId!,
            EventType = VerificationWebhookV1.EventType,
            PayloadJson = payload,
            CreatedAt = WebhookDeadLetteredAt.AddMinutes(-1),
            NextAttemptAt = WebhookDeadLetteredAt.AddMinutes(-1),
            DeadLetteredAt = WebhookDeadLetteredAt,
            AttemptCount = 8,
            LastErrorCode = "WEBHOOK_HTTP_503"
        };
        db.AuditVerificationJobs.Add(job);
        db.WebhookDeliveries.Add(delivery);
        await db.SaveChangesAsync();
        return new SeededWebhook(
            delivery.Id,
            payload,
            WebhookDeadLetteredAt,
            delivery.AttemptCount,
            delivery.LastErrorCode!);
    }

    private static async Task<Guid> AddTerminalVerificationJobAsync(CaseLedgerFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var caseRecord = await db.Cases.SingleAsync(item => item.Reference == "CL-2026-003");
        var job = NewQueuedJob(caseRecord);
        job.Status = AuditVerificationJobStatus.DeadLettered;
        job.ResultId = $"audit-verification:{job.Id:D}:v1";
        job.ErrorCode = "RETRY_EXHAUSTED";
        job.CompletedAt = RequestDeadLetteredAt.AddMinutes(1);
        db.AuditVerificationJobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    private static AuditVerificationJob NewQueuedJob(CaseRecord caseRecord)
    {
        var id = Guid.NewGuid();
        return new AuditVerificationJob
        {
            Id = id,
            CaseId = caseRecord.Id,
            Case = caseRecord,
            Status = AuditVerificationJobStatus.Queued,
            TargetSequence = caseRecord.AuditHeadSequence,
            TargetHash = caseRecord.AuditHeadHash,
            SnapshotSha256 = new string('a', 64),
            RequestedAt = RequestDeadLetteredAt.AddMinutes(-2)
        };
    }

    private static AuditVerificationJob NewCompletedJob(CaseRecord caseRecord)
    {
        var job = NewQueuedJob(caseRecord);
        job.Status = AuditVerificationJobStatus.Completed;
        job.ResultId = $"audit-verification:{job.Id:D}:v1";
        job.Valid = true;
        job.CheckedEvents = caseRecord.AuditHeadSequence;
        job.ChainHead = caseRecord.AuditHeadHash;
        job.CompletedAt = WebhookDeadLetteredAt.AddMinutes(-2);
        return job;
    }

    private static async Task UpdateRequestAsync(
        CaseLedgerFactory factory,
        Guid id,
        Action<OutboxMessage> update)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var outbox = await db.OutboxMessages.SingleAsync(item => item.Id == id);
        update(outbox);
        await db.SaveChangesAsync();
    }

    private sealed record SeededOutbox(
        Guid Id,
        string PayloadJson,
        DateTime DeadLetteredAt,
        int AttemptCount,
        string LastErrorCode);

    private sealed record SeededWebhook(
        Guid Id,
        string PayloadJson,
        DateTime DeadLetteredAt,
        int AttemptCount,
        string LastErrorCode);
}
