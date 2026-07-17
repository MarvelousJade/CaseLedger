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
            AppendUtf8(
                hash,
                auditEvent.GetProperty("sequence").GetInt32().ToString(CultureInfo.InvariantCulture));
            AppendUtf8(hash, "\n");
            AppendUtf8(hash, auditEvent.GetProperty("previousHash").GetString()!);
            AppendUtf8(hash, "\n");
            AppendUtf8(hash, auditEvent.GetProperty("hash").GetString()!);
            AppendUtf8(hash, "\n");
            var canonicalData = Encoding.UTF8.GetBytes(
                auditEvent.GetProperty("canonicalData").GetString()!);
            AppendUtf8(hash, canonicalData.Length.ToString(CultureInfo.InvariantCulture));
            AppendUtf8(hash, "\n");
            hash.AppendData(canonicalData);
            AppendUtf8(hash, "\n");
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendUtf8(IncrementalHash hash, string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(value));
}
