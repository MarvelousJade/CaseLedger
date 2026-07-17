using System.Net;
using System.Net.Http.Json;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CaseLedger.Api.Contracts;
using CaseLedger.Api.Data;
using CaseLedger.Api.Domain;
using CaseLedger.Api.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaseLedger.Api.Tests;

public sealed class AuditVerificationJobTests
{
    [Fact]
    public async Task EvidenceStagesImmutableVerificationRequestWhenEnabled()
    {
        using var factory = new CaseLedgerFactory(messagingEnabled: true);
        using var client = factory.CreateCookieClient();
        await client.LoginAsync();
        var target = await FindCaseAsync(client, "CL-2026-002");
        var digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes("phase-two-evidence")))
            .ToLowerInvariant();

        var response = await client.PostAsJsonAsync(
            $"/api/cases/{target.Id:D}/evidence",
            new AddEvidenceRequest(
                "phase-two.json",
                2_048,
                "application/json",
                digest));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var caseRecord = await db.Cases.AsNoTracking().SingleAsync(item => item.Id == target.Id);
        var job = await db.AuditVerificationJobs
            .AsNoTracking()
            .SingleAsync(item => item.CaseId == target.Id);
        var outbox = await db.OutboxMessages
            .AsNoTracking()
            .SingleAsync(item => item.Id == job.Id);
        var sourceEvents = await db.AuditEvents
            .AsNoTracking()
            .Where(item => item.CaseId == target.Id)
            .OrderBy(item => item.Sequence)
            .ToArrayAsync();

        Assert.Equal(AuditVerificationJobStatus.Queued, job.Status);
        Assert.Equal(caseRecord.AuditHeadSequence, job.TargetSequence);
        Assert.Equal(caseRecord.AuditHeadHash, job.TargetHash);
        Assert.Equal(AuditVerificationScheduler.RequestMessageType, outbox.MessageType);
        Assert.Null(outbox.PublishedAt);
        Assert.Equal(0, outbox.AttemptCount);

        using var payload = JsonDocument.Parse(outbox.PayloadJson);
        var root = payload.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            AuditVerificationScheduler.RequestMessageType,
            root.GetProperty("messageType").GetString());
        Assert.Equal(job.Id, root.GetProperty("messageId").GetGuid());
        Assert.Equal(job.Id, root.GetProperty("jobId").GetGuid());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("correlationId").GetString()));

        var data = root.GetProperty("data");
        Assert.Equal(target.Id, data.GetProperty("caseId").GetGuid());
        Assert.Equal(
            AuditVerificationScheduler.VerificationProfile,
            data.GetProperty("verificationProfile").GetString());
        Assert.Equal(job.TargetSequence, data.GetProperty("targetSequence").GetInt32());
        Assert.Equal(job.TargetHash, data.GetProperty("targetHash").GetString());
        var snapshot = data.GetProperty("snapshot");
        Assert.Equal(2, snapshot.GetProperty("eventCount").GetInt32());
        Assert.Equal(
            [1, 2],
            snapshot.GetProperty("events")
                .EnumerateArray()
                .Select(item => item.GetProperty("sequence").GetInt32())
                .ToArray());
        var projectedEvents = snapshot.GetProperty("events").EnumerateArray().ToArray();
        Assert.Equal(sourceEvents.Length, projectedEvents.Length);
        for (var index = 0; index < sourceEvents.Length; index++)
        {
            var source = sourceEvents[index];
            var projected = projectedEvents[index];
            Assert.Equal(source.Id.ToString("D"), projected.GetProperty("eventId").GetString());
            Assert.Equal(source.CaseId.ToString("D"), projected.GetProperty("caseId").GetString());
            Assert.Equal(source.Sequence, projected.GetProperty("sequence").GetInt32());
            Assert.Equal(source.EventType, projected.GetProperty("eventType").GetString());
            Assert.Equal(source.Description, projected.GetProperty("description").GetString());
            Assert.Equal(source.ActorId.ToString("D"), projected.GetProperty("actorId").GetString());
            Assert.Equal(source.ActorName, projected.GetProperty("actorName").GetString());
            Assert.Equal(
                FormatUtc(source.CreatedAt),
                projected.GetProperty("createdAt").GetString());
            Assert.Equal(
                source.PreviousHash,
                projected.GetProperty("previousHash").GetString());
            Assert.Equal(source.Hash, projected.GetProperty("hash").GetString());
            Assert.Equal(
                source.CanonicalData,
                projected.GetProperty("canonicalData").GetString());
        }
        var snapshotSha256 = ComputeSnapshotSha256(snapshot.GetProperty("events"));
        Assert.Equal(job.SnapshotSha256, snapshotSha256);
    }

    [Fact]
    public async Task ManualQueueExposesCurrentThenStaleJobAndCommentsDoNotAutoSchedule()
    {
        using var factory = new CaseLedgerFactory(messagingEnabled: true);
        using var client = factory.CreateCookieClient();
        await client.LoginAsync();
        var target = await FindCaseAsync(client, "CL-2026-003");

        var queuedResponse = await client.PostAsync(
            $"/api/cases/{target.Id:D}/audit/verifications",
            content: null);

        Assert.Equal(HttpStatusCode.Accepted, queuedResponse.StatusCode);
        var queued = await queuedResponse.Content.ReadRequiredJsonAsync<AuditVerificationJobResponse>();
        Assert.Equal("Queued", queued.Status);
        Assert.True(queued.IsCurrent);
        Assert.EndsWith(
            $"/api/cases/{target.Id:D}/audit/verifications/{queued.Id:D}",
            queuedResponse.Headers.Location?.OriginalString);

        var byId = await client.GetFromJsonAsync<AuditVerificationJobResponse>(
            $"/api/cases/{target.Id:D}/audit/verifications/{queued.Id:D}");
        var latest = await client.GetFromJsonAsync<AuditVerificationJobResponse>(
            $"/api/cases/{target.Id:D}/audit/verifications/latest");
        Assert.NotNull(byId);
        Assert.NotNull(latest);
        Assert.Equal(queued.Id, byId.Id);
        Assert.Equal(queued.Id, latest.Id);
        Assert.True(byId.IsCurrent);

        var commentResponse = await client.PostAsJsonAsync(
            $"/api/cases/{target.Id:D}/comments",
            new AddCommentRequest("Advance the audit head without scheduling another verification."));
        Assert.Equal(HttpStatusCode.Created, commentResponse.StatusCode);

        var stale = await client.GetFromJsonAsync<AuditVerificationJobResponse>(
            $"/api/cases/{target.Id:D}/audit/verifications/{queued.Id:D}");
        Assert.NotNull(stale);
        Assert.False(stale.IsCurrent);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        Assert.Equal(
            1,
            await db.AuditVerificationJobs.CountAsync(item => item.CaseId == target.Id));
        Assert.Equal(
            1,
            await db.OutboxMessages.CountAsync(item => item.Id == queued.Id));
    }

    [Fact]
    public async Task DisabledMessagingRejectsManualQueueWithoutBlockingEvidence()
    {
        using var factory = new CaseLedgerFactory();
        using var client = factory.CreateCookieClient();
        await client.LoginAsync();
        var target = await FindCaseAsync(client, "CL-2026-004");

        var queueResponse = await client.PostAsync(
            $"/api/cases/{target.Id:D}/audit/verifications",
            content: null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, queueResponse.StatusCode);

        var digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes("disabled-messaging-evidence")))
            .ToLowerInvariant();
        var evidenceResponse = await client.PostAsJsonAsync(
            $"/api/cases/{target.Id:D}/evidence",
            new AddEvidenceRequest(
                "disabled-messaging.txt",
                1_024,
                "text/plain",
                digest));
        Assert.Equal(HttpStatusCode.Created, evidenceResponse.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        Assert.False(await db.AuditVerificationJobs.AnyAsync());
        Assert.False(await db.OutboxMessages.AnyAsync());
        Assert.True(await db.Evidence.AnyAsync(item => item.CaseId == target.Id));
    }

    [Fact]
    public async Task SnapshotProjectionAndDigestReflectDenormalizedSourceTampering()
    {
        using var factory = new CaseLedgerFactory(messagingEnabled: true);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var scheduler =
            scope.ServiceProvider.GetRequiredService<AuditVerificationScheduler>();
        var target = await db.Cases
            .AsNoTracking()
            .SingleAsync(item => item.Reference == "CL-2026-001");
        const string tamperedDescription =
            "Description changed outside the immutable application path.";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "AuditEvents" SET "Description" = {tamperedDescription} WHERE "CaseId" = {target.Id} AND "Sequence" = 1""");

        var job = await scheduler.ScheduleAsync(target.Id, "projection-test");
        await db.SaveChangesAsync();
        var outbox = await db.OutboxMessages
            .AsNoTracking()
            .SingleAsync(item => item.Id == job.Id);

        using var payload = JsonDocument.Parse(outbox.PayloadJson);
        var events = payload.RootElement
            .GetProperty("data")
            .GetProperty("snapshot")
            .GetProperty("events");
        var projected = events[0];
        Assert.Equal(
            tamperedDescription,
            projected.GetProperty("description").GetString());
        using var canonical = JsonDocument.Parse(
            projected.GetProperty("canonicalData").GetString()!);
        Assert.NotEqual(
            tamperedDescription,
            canonical.RootElement.GetProperty("description").GetString());
        Assert.Equal(job.SnapshotSha256, ComputeSnapshotSha256(events));
    }

    [Fact]
    public void SnapshotDigestMatchesCrossLanguageNonAsciiVector()
    {
        AuditVerificationSnapshotEventV1[] events =
        [
            new(
                "11111111-1111-4111-8111-111111111111",
                "22222222-2222-4222-8222-222222222222",
                1,
                "case.created",
                "Montréal <>& 雪",
                "33333333-3333-4333-8333-333333333333",
                "Élodie",
                "2026-07-16T18:00:00.0000000Z",
                new string('0', 64),
                new string('a', 64),
                "{\"label\":\"Montréal <>& 雪\"}")
        ];

        Assert.Equal(
            "0b341c88308bd99a76b00352fe951a615330212a8a4f2f1061aaadd14ba68a90",
            AuditVerificationScheduler.ComputeSnapshotSha256(events));
    }

    private static async Task<CaseListItemResponse> FindCaseAsync(
        HttpClient client,
        string reference)
    {
        var cases = await client.GetFromJsonAsync<CaseCollectionResponse>("/api/cases?limit=100");
        Assert.NotNull(cases);
        return Assert.Single(cases.Items, item => item.Reference == reference);
    }

    private static string ComputeSnapshotSha256(JsonElement events)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var auditEvent in events.EnumerateArray())
        {
            AppendFramed(hash, auditEvent.GetProperty("eventId").GetString()!);
            AppendFramed(hash, auditEvent.GetProperty("caseId").GetString()!);
            AppendFramed(
                hash,
                auditEvent.GetProperty("sequence")
                    .GetInt32()
                    .ToString(CultureInfo.InvariantCulture));
            AppendFramed(hash, auditEvent.GetProperty("eventType").GetString()!);
            AppendFramed(hash, auditEvent.GetProperty("description").GetString()!);
            AppendFramed(hash, auditEvent.GetProperty("actorId").GetString()!);
            AppendFramed(hash, auditEvent.GetProperty("actorName").GetString()!);
            AppendFramed(hash, auditEvent.GetProperty("createdAt").GetString()!);
            AppendFramed(hash, auditEvent.GetProperty("previousHash").GetString()!);
            AppendFramed(hash, auditEvent.GetProperty("hash").GetString()!);
            AppendFramed(hash, auditEvent.GetProperty("canonicalData").GetString()!);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendUtf8(IncrementalHash hash, string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(value));

    private static void AppendFramed(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        AppendUtf8(hash, bytes.Length.ToString(CultureInfo.InvariantCulture));
        AppendUtf8(hash, "\n");
        hash.AppendData(bytes);
        AppendUtf8(hash, "\n");
    }

    private static string FormatUtc(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
        var normalized = new DateTime(
            utc.Ticks - utc.Ticks % TimeSpan.TicksPerMicrosecond,
            DateTimeKind.Utc);
        return normalized.ToString("O", CultureInfo.InvariantCulture);
    }
}
