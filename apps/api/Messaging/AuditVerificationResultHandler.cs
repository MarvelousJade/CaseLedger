using System.Text.RegularExpressions;
using CaseLedger.Api.Data;
using CaseLedger.Api.Domain;
using CaseLedger.Api.Realtime;
using CaseLedger.Api.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CaseLedger.Api.Messaging;

public enum ResultApplyDisposition
{
    Applied,
    Duplicate
}

public interface IAuditVerificationResultApplier
{
    Task<ResultApplyDisposition> HandleAsync(
        AuditVerificationResultV1 result,
        CancellationToken cancellationToken = default);
}

public sealed partial class AuditVerificationResultHandler(
    CaseLedgerDbContext db,
    IVerificationUpdateNotifier notifier,
    IOptions<WebhookOptions>? webhookOptions = null,
    TimeProvider? timeProvider = null)
    : IAuditVerificationResultApplier
{
    public async Task<ResultApplyDisposition> HandleAsync(
        AuditVerificationResultV1 result,
        CancellationToken cancellationToken = default)
    {
        AuditVerificationResultContract.Validate(result);
        var job = await db.AuditVerificationJobs.SingleOrDefaultAsync(
            item => item.Id == result.JobId,
            cancellationToken);
        if (job is null)
        {
            throw Invalid("JOB_NOT_FOUND");
        }

        if (job.CaseId != result.CaseId ||
            !string.Equals(
                result.VerificationProfile,
                AuditVerificationScheduler.VerificationProfile,
                StringComparison.Ordinal) ||
            !string.Equals(
                job.SnapshotSha256,
                result.SnapshotSha256,
                StringComparison.Ordinal))
        {
            throw Invalid("RESULT_JOB_MISMATCH");
        }

        if (await db.AuditVerificationJobs
            .AsNoTracking()
            .AnyAsync(
                item => item.Id != job.Id && item.ResultId == result.ResultId,
                cancellationToken))
        {
            throw Invalid("RESULT_ID_COLLISION");
        }

        var completedAt = NormalizeUtcToMicroseconds(result.CompletedAt.UtcDateTime);
        var expected = ExpectedState(result, completedAt);
        if (job.ResultId is not null)
        {
            if (!string.Equals(job.ResultId, result.ResultId, StringComparison.Ordinal) ||
                !Matches(job, expected, result))
            {
                throw Invalid("RESULT_ID_COLLISION");
            }

            await notifier.NotifyAsync(ToUpdate(job), cancellationToken);
            return ResultApplyDisposition.Duplicate;
        }

        job.ResultId = result.ResultId;
        job.Status = expected.Status;
        job.Valid = expected.Valid;
        job.CheckedEvents = result.CheckedEvents;
        job.BrokenAt = result.BrokenAt;
        job.ChainHead = result.ChainHead;
        job.ErrorCode = expected.ErrorCode;
        job.CompletedAt = completedAt;
        if (webhookOptions?.Value.Enabled == true)
        {
            var createdAt = NormalizeUtcToMicroseconds(
                (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime);
            db.WebhookDeliveries.Add(
                VerificationWebhookV1.CreateDelivery(job, createdAt));
        }
        await db.SaveChangesAsync(cancellationToken);

        await notifier.NotifyAsync(ToUpdate(job), cancellationToken);
        return ResultApplyDisposition.Applied;
    }

    private static ExpectedJobState ExpectedState(
        AuditVerificationResultV1 result,
        DateTime completedAt) => result.Outcome switch
    {
        "valid" => new(
            AuditVerificationJobStatus.Completed,
            true,
            null,
            completedAt),
        "invalid" => new(
            AuditVerificationJobStatus.Completed,
            false,
            result.Error is null
                ? null
                : SanitizeErrorCode(result.Error.Code),
            completedAt),
        "error" => new(
            AuditVerificationJobStatus.DeadLettered,
            null,
            SanitizeErrorCode(result.Error!.Code),
            completedAt),
        _ => throw Invalid("RESULT_SCHEMA_INVALID")
    };

    private static bool Matches(
        AuditVerificationJob job,
        ExpectedJobState expected,
        AuditVerificationResultV1 result) =>
        job.Status == expected.Status &&
        job.Valid == expected.Valid &&
        job.CheckedEvents == result.CheckedEvents &&
        job.BrokenAt == result.BrokenAt &&
        string.Equals(job.ChainHead, result.ChainHead, StringComparison.Ordinal) &&
        job.CompletedAt == expected.CompletedAt &&
        string.Equals(job.ErrorCode, expected.ErrorCode, StringComparison.Ordinal);

    private static VerificationUpdatedDto ToUpdate(AuditVerificationJob job) =>
        new(
            job.Id,
            job.CaseId,
            job.ResultId!,
            job.Status.ToString(),
            job.Valid,
            job.CheckedEvents,
            job.BrokenAt);

    private static string SanitizeErrorCode(string value) =>
        ErrorCodeRegex().IsMatch(value) ? value : "WORKER_ERROR";

    private static DateTime NormalizeUtcToMicroseconds(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc
            ? value
            : value.ToUniversalTime();
        return new DateTime(
            utc.Ticks - utc.Ticks % TimeSpan.TicksPerMicrosecond,
            DateTimeKind.Utc);
    }

    private static ResultMessageValidationException Invalid(string code) =>
        new(code, "The verification result cannot be applied to the requested job.");

    private sealed record ExpectedJobState(
        AuditVerificationJobStatus Status,
        bool? Valid,
        string? ErrorCode,
        DateTime CompletedAt);

    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,79}$", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorCodeRegex();
}
