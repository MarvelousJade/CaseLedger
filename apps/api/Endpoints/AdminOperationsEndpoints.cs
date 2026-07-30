using System.Data;
using System.Security.Claims;
using CaseLedger.Api.Contracts;
using CaseLedger.Api.Data;
using CaseLedger.Api.Domain;
using CaseLedger.Api.Messaging;
using CaseLedger.Api.Webhooks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CaseLedger.Api.Endpoints;

public static class AdminOperationsEndpoints
{
    private const string VerificationRequestKind = "verification-request";
    private const string VerificationJobKind = "verification-job";
    private const string WebhookDeliveryKind = "webhook-delivery";

    public static RouteGroupBuilder MapAdminOperationsEndpoints(this RouteGroupBuilder api)
    {
        var operations = api.MapGroup("/admin/operations")
            .RequireAuthorization(policy => policy.RequireRole("Admin"))
            .RequireRateLimiting("authenticated")
            .WithTags("Administration");

        operations.MapGet("/failures", GetFailuresAsync)
            .WithName("ListOperationalFailures")
            .WithSummary("List redacted delivery failures requiring operator attention")
            .Produces<OperationalFailureCollectionResponse>()
            .Produces<HttpValidationProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status429TooManyRequests);

        operations.MapPost(
                "/verification-requests/{id:guid}/replay",
                ReplayVerificationRequestAsync)
            .WithName("ReplayDeadLetteredVerificationRequest")
            .WithSummary("Reactivate a dead-lettered verification request publication")
            .Produces<OperationalReplayResponse>(StatusCodes.Status202Accepted)
            .Produces<HttpValidationProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status429TooManyRequests);

        operations.MapPost(
                "/webhooks/{id:guid}/replay",
                ReplayWebhookAsync)
            .WithName("ReplayDeadLetteredWebhook")
            .WithSummary("Reactivate a dead-lettered webhook delivery")
            .Produces<OperationalReplayResponse>(StatusCodes.Status202Accepted)
            .Produces<HttpValidationProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status429TooManyRequests);

        return api;
    }

    private static async Task<IResult> GetFailuresAsync(
        [AsParameters] OperationalFailureListQuery request,
        CaseLedgerDbContext db,
        IOptions<MessagingOptions> messagingOptions,
        IOptions<WebhookOptions> webhookOptions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var errors = ValidateListQuery(request, out var selectedKind, out var offset);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var queryLimit = offset + request.PageSize;
        var now = UtcTimestamp.Now(timeProvider);
        var failures = new List<OperationalFailureResponse>();
        var total = 0;

        if (selectedKind is null or VerificationRequestKind)
        {
            var requestQuery = db.OutboxMessages
                .AsNoTracking()
                .Where(item =>
                    item.MessageType == AuditVerificationScheduler.RequestMessageType &&
                    item.DeadLetteredAt != null);
            total += await requestQuery.CountAsync(cancellationToken);
            var requestRows = await requestQuery
                .OrderByDescending(item => item.DeadLetteredAt)
                .ThenBy(item => item.Id)
                .Take(queryLimit)
                .Select(item => new RequestFailureRow(
                    item.Id,
                    item.OccurredAt,
                    item.DeadLetteredAt!.Value,
                    item.PublishedAt,
                    item.AttemptCount,
                    item.LockedUntil,
                    item.LastErrorCode))
                .ToListAsync(cancellationToken);
            var requestIds = requestRows.Select(item => item.Id).ToArray();
            var jobLookup = await db.AuditVerificationJobs
                .AsNoTracking()
                .Where(item => requestIds.Contains(item.Id))
                .Select(item => new RequestJobRow(
                    item.Id,
                    item.CaseId,
                    item.Case.Reference,
                    item.Status,
                    item.ResultId))
                .ToDictionaryAsync(item => item.Id, cancellationToken);

            foreach (var row in requestRows)
            {
                jobLookup.TryGetValue(row.Id, out var job);
                var blockReason = VerificationRequestReplayBlockReason(
                    row,
                    job,
                    messagingOptions.Value.Enabled,
                    now);
                failures.Add(new OperationalFailureResponse(
                    VerificationRequestKind,
                    row.Id,
                    job?.CaseId,
                    job?.CaseReference,
                    job?.Id,
                    row.OccurredAt,
                    row.DeadLetteredAt,
                    row.AttemptCount,
                    row.LastErrorCode,
                    blockReason is null,
                    blockReason));
            }
        }

        if (selectedKind is null or VerificationJobKind)
        {
            var jobQuery = db.AuditVerificationJobs
                .AsNoTracking()
                .Where(item => item.Status == AuditVerificationJobStatus.DeadLettered);
            total += await jobQuery.CountAsync(cancellationToken);
            var jobRows = await jobQuery
                .OrderByDescending(item => item.CompletedAt)
                .ThenBy(item => item.Id)
                .Take(queryLimit)
                .Select(item => new TerminalJobFailureRow(
                    item.Id,
                    item.CaseId,
                    item.Case.Reference,
                    item.RequestedAt,
                    item.CompletedAt,
                    item.ErrorCode))
                .ToListAsync(cancellationToken);
            failures.AddRange(jobRows.Select(row => new OperationalFailureResponse(
                VerificationJobKind,
                row.Id,
                row.CaseId,
                row.CaseReference,
                row.Id,
                row.RequestedAt,
                row.CompletedAt,
                null,
                row.ErrorCode,
                false,
                "Terminal verification jobs are immutable; queue a new verification instead.")));
        }

        if (selectedKind is null or WebhookDeliveryKind)
        {
            var webhookQuery = db.WebhookDeliveries
                .AsNoTracking()
                .Where(item => item.DeadLetteredAt != null);
            total += await webhookQuery.CountAsync(cancellationToken);
            var webhookRows = await webhookQuery
                .OrderByDescending(item => item.DeadLetteredAt)
                .ThenBy(item => item.Id)
                .Take(queryLimit)
                .Select(item => new WebhookFailureRow(
                    item.Id,
                    item.VerificationJobId,
                    item.VerificationJob.CaseId,
                    item.VerificationJob.Case.Reference,
                    item.CreatedAt,
                    item.DeadLetteredAt!.Value,
                    item.DeliveredAt,
                    item.AttemptCount,
                    item.LockedUntil,
                    item.LastErrorCode))
                .ToListAsync(cancellationToken);
            foreach (var row in webhookRows)
            {
                var blockReason = WebhookReplayBlockReason(
                    row,
                    webhookOptions.Value.Enabled,
                    now);
                failures.Add(new OperationalFailureResponse(
                    WebhookDeliveryKind,
                    row.Id,
                    row.CaseId,
                    row.CaseReference,
                    row.VerificationJobId,
                    row.CreatedAt,
                    row.DeadLetteredAt,
                    row.AttemptCount,
                    row.LastErrorCode,
                    blockReason is null,
                    blockReason));
            }
        }

        var pageItems = failures
            .OrderByDescending(item => item.DeadLetteredAt ?? item.OccurredAt)
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Id)
            .Skip(offset)
            .Take(request.PageSize)
            .ToArray();
        var totalPages = total == 0
            ? 0
            : (int)Math.Ceiling(total / (double)request.PageSize);
        return Results.Ok(new OperationalFailureCollectionResponse(
            pageItems,
            total,
            request.Page,
            request.PageSize,
            totalPages));
    }

    private static async Task<IResult> ReplayVerificationRequestAsync(
        Guid id,
        OperationalReplayRequest request,
        ClaimsPrincipal principal,
        CaseLedgerDbContext db,
        IOptions<MessagingOptions> messagingOptions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var errors = ValidateReplayRequest(request, out var expectedDeadLetteredAt, out var reason);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        if (!messagingOptions.Value.Enabled)
        {
            return RuntimeDisabled("Verification messaging is disabled.");
        }

        var actor = await ApiEndpoints.GetCurrentUserAsync(principal, db, cancellationToken);
        if (actor is null)
        {
            return AuthenticationRequired();
        }

        var now = UtcTimestamp.Now(timeProvider);
        return await ExecuteReplayAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(
                GetReplayIsolationLevel(db),
                cancellationToken);
            var source = await db.OutboxMessages
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
            if (source is null ||
                !string.Equals(
                    source.MessageType,
                    AuditVerificationScheduler.RequestMessageType,
                    StringComparison.Ordinal))
            {
                return Results.NotFound(new ProblemDetails
                {
                    Status = StatusCodes.Status404NotFound,
                    Title = "Verification request not found"
                });
            }

            if (source.DeadLetteredAt is null ||
                source.DeadLetteredAt.Value != expectedDeadLetteredAt ||
                source.PublishedAt is not null ||
                HasActiveLease(source.LockedUntil, now))
            {
                return ReplayConflict();
            }

            var guardedJobs = await db.AuditVerificationJobs
                .Where(item =>
                    item.Id == id &&
                    item.Status == AuditVerificationJobStatus.Queued &&
                    item.ResultId == null)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(item => item.Status, item => item.Status),
                    cancellationToken);
            if (guardedJobs != 1)
            {
                return ReplayConflict();
            }

            var updated = await db.OutboxMessages
                .Where(item =>
                    item.Id == id &&
                    item.MessageType == AuditVerificationScheduler.RequestMessageType &&
                    item.DeadLetteredAt == expectedDeadLetteredAt &&
                    item.PublishedAt == null &&
                    (item.LockedUntil == null || item.LockedUntil < now))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(item => item.DeadLetteredAt, (DateTime?)null)
                        .SetProperty(item => item.AttemptCount, 0)
                        .SetProperty(item => item.NextAttemptAt, now)
                        .SetProperty(item => item.LockId, (Guid?)null)
                        .SetProperty(item => item.LockedUntil, (DateTime?)null)
                        .SetProperty(item => item.LastErrorCode, (string?)null),
                    cancellationToken);
            if (updated != 1)
            {
                return ReplayConflict();
            }

            var replay = NewReplay(
                OperationalReplayKind.VerificationRequest,
                source,
                actor.Id,
                expectedDeadLetteredAt,
                now,
                reason);
            db.OperationalReplays.Add(replay);
            if (!await SaveReplayAuditAsync(db, transaction, cancellationToken))
            {
                return ReplayConflict();
            }

            return Results.Accepted(
                value: ToReplayResponse(replay));
        });
    }

    private static async Task<IResult> ReplayWebhookAsync(
        Guid id,
        OperationalReplayRequest request,
        ClaimsPrincipal principal,
        CaseLedgerDbContext db,
        IOptions<WebhookOptions> webhookOptions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var errors = ValidateReplayRequest(request, out var expectedDeadLetteredAt, out var reason);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        if (!webhookOptions.Value.Enabled)
        {
            return RuntimeDisabled("Webhook delivery is disabled.");
        }

        var actor = await ApiEndpoints.GetCurrentUserAsync(principal, db, cancellationToken);
        if (actor is null)
        {
            return AuthenticationRequired();
        }

        var now = UtcTimestamp.Now(timeProvider);
        return await ExecuteReplayAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(
                GetReplayIsolationLevel(db),
                cancellationToken);
            var source = await db.WebhookDeliveries
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
            if (source is null)
            {
                return Results.NotFound(new ProblemDetails
                {
                    Status = StatusCodes.Status404NotFound,
                    Title = "Webhook delivery not found"
                });
            }

            if (source.DeadLetteredAt is null ||
                source.DeadLetteredAt.Value != expectedDeadLetteredAt ||
                source.DeliveredAt is not null ||
                HasActiveLease(source.LockedUntil, now))
            {
                return ReplayConflict();
            }

            var updated = await db.WebhookDeliveries
                .Where(item =>
                    item.Id == id &&
                    item.DeadLetteredAt == expectedDeadLetteredAt &&
                    item.DeliveredAt == null &&
                    (item.LockedUntil == null || item.LockedUntil < now))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(item => item.DeadLetteredAt, (DateTime?)null)
                        .SetProperty(item => item.AttemptCount, 0)
                        .SetProperty(item => item.NextAttemptAt, now)
                        .SetProperty(item => item.LockId, (Guid?)null)
                        .SetProperty(item => item.LockedUntil, (DateTime?)null)
                        .SetProperty(item => item.LastErrorCode, (string?)null),
                    cancellationToken);
            if (updated != 1)
            {
                return ReplayConflict();
            }

            var replay = NewReplay(
                OperationalReplayKind.WebhookDelivery,
                source,
                actor.Id,
                expectedDeadLetteredAt,
                now,
                reason);
            db.OperationalReplays.Add(replay);
            if (!await SaveReplayAuditAsync(db, transaction, cancellationToken))
            {
                return ReplayConflict();
            }

            return Results.Accepted(
                value: ToReplayResponse(replay));
        });
    }

    private static Dictionary<string, string[]> ValidateListQuery(
        OperationalFailureListQuery request,
        out string? selectedKind,
        out int offset)
    {
        var errors = new Dictionary<string, string[]>();
        selectedKind = string.IsNullOrWhiteSpace(request.Kind)
            ? null
            : request.Kind.Trim().ToLowerInvariant();
        if (selectedKind is not null &&
            selectedKind is not VerificationRequestKind and
                not VerificationJobKind and
                not WebhookDeliveryKind)
        {
            errors["kind"] =
            [
                $"kind must be {VerificationRequestKind}, {VerificationJobKind}, or {WebhookDeliveryKind}."
            ];
        }

        if (request.Page < 1)
        {
            errors["page"] = ["page must be 1 or greater."];
        }

        if (request.PageSize is < 1 or > 100)
        {
            errors["pageSize"] = ["pageSize must be between 1 and 100."];
        }

        var calculatedOffset = (long)(request.Page - 1) * request.PageSize;
        if (calculatedOffset is < 0 or > 10_000)
        {
            errors["page"] = ["page is too large for the requested pageSize."];
            offset = 0;
        }
        else
        {
            offset = (int)calculatedOffset;
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateReplayRequest(
        OperationalReplayRequest request,
        out DateTime expectedDeadLetteredAt,
        out string reason)
    {
        var errors = new Dictionary<string, string[]>();
        expectedDeadLetteredAt = default;
        if (request.DeadLetteredAt is null)
        {
            errors["deadLetteredAt"] = ["deadLetteredAt is required."];
        }
        else
        {
            expectedDeadLetteredAt = UtcTimestamp.Normalize(request.DeadLetteredAt.Value);
        }

        reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is < 3 or > 240 || reason.Any(char.IsControl))
        {
            errors["reason"] =
                ["reason must contain between 3 and 240 characters without control characters."];
        }

        return errors;
    }

    private static string? VerificationRequestReplayBlockReason(
        RequestFailureRow source,
        RequestJobRow? job,
        bool messagingEnabled,
        DateTime now)
    {
        if (!messagingEnabled)
        {
            return "Verification messaging is disabled.";
        }

        if (source.PublishedAt is not null)
        {
            return "The request has conflicting published and dead-letter states.";
        }

        if (HasActiveLease(source.LockedUntil, now))
        {
            return "The request has an active dispatcher lease.";
        }

        if (job is null)
        {
            return "The associated verification job was not found.";
        }

        if (job.Status != AuditVerificationJobStatus.Queued || job.ResultId is not null)
        {
            return "The associated verification job is already terminal.";
        }

        return null;
    }

    private static string? WebhookReplayBlockReason(
        WebhookFailureRow source,
        bool webhookEnabled,
        DateTime now)
    {
        if (!webhookEnabled)
        {
            return "Webhook delivery is disabled.";
        }

        if (source.DeliveredAt is not null)
        {
            return "The webhook has conflicting delivered and dead-letter states.";
        }

        return HasActiveLease(source.LockedUntil, now)
            ? "The webhook has an active dispatcher lease."
            : null;
    }

    private static OperationalReplay NewReplay(
        OperationalReplayKind kind,
        OutboxMessage source,
        Guid actorId,
        DateTime sourceDeadLetteredAt,
        DateTime replayedAt,
        string reason) => new()
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            SourceId = source.Id,
            SourceDeadLetteredAt = sourceDeadLetteredAt,
            ActorId = actorId,
            ReplayedAt = replayedAt,
            PreviousAttemptCount = source.AttemptCount,
            PreviousErrorCode = source.LastErrorCode,
            Reason = reason
        };

    private static OperationalReplay NewReplay(
        OperationalReplayKind kind,
        WebhookDelivery source,
        Guid actorId,
        DateTime sourceDeadLetteredAt,
        DateTime replayedAt,
        string reason) => new()
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            SourceId = source.Id,
            SourceDeadLetteredAt = sourceDeadLetteredAt,
            ActorId = actorId,
            ReplayedAt = replayedAt,
            PreviousAttemptCount = source.AttemptCount,
            PreviousErrorCode = source.LastErrorCode,
            Reason = reason
        };

    private static IsolationLevel GetReplayIsolationLevel(CaseLedgerDbContext db)
    {
        // PostgreSQL rechecks an UPDATE predicate after waiting for a concurrent row writer at
        // READ COMMITTED. That gives the guarded updates compare-and-swap semantics without
        // turning the losing replay into an expected SQLSTATE 40001 serialization failure.
        // SQLite only supports SERIALIZABLE/READ UNCOMMITTED and serializes these test writes.
        return db.Database.IsNpgsql()
            ? IsolationLevel.ReadCommitted
            : IsolationLevel.Serializable;
    }

    private static async Task<IResult> ExecuteReplayAsync(Func<Task<IResult>> replay)
    {
        try
        {
            return await replay();
        }
        catch (Exception exception) when (IsPostgresReplayConflict(exception))
        {
            // A deployment may still run with stricter connection/transaction defaults. Treat
            // PostgreSQL concurrency aborts as the same stale-state conflict as a failed guard.
            return ReplayConflict();
        }
    }

    private static bool IsPostgresReplayConflict(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgresException &&
                postgresException.SqlState is
                    PostgresErrorCodes.SerializationFailure or
                    PostgresErrorCodes.DeadlockDetected)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> SaveReplayAuditAsync(
        CaseLedgerDbContext db,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
    }

    private static OperationalReplayResponse ToReplayResponse(OperationalReplay replay) =>
        new(
            replay.Id,
            replay.Kind == OperationalReplayKind.VerificationRequest
                ? VerificationRequestKind
                : WebhookDeliveryKind,
            replay.SourceId,
            replay.SourceDeadLetteredAt,
            replay.ReplayedAt);

    private static bool HasActiveLease(DateTime? lockedUntil, DateTime now) =>
        lockedUntil is not null && lockedUntil >= now;

    private static IResult ReplayConflict() => Results.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Failure cannot be replayed",
        detail: "The failure changed, has an active lease, has conflicting terminal state, or is no longer replayable.");

    private static IResult RuntimeDisabled(string detail) => Results.Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        title: "Delivery runtime is disabled",
        detail: detail);

    private static IResult AuthenticationRequired() => Results.Problem(
        statusCode: StatusCodes.Status401Unauthorized,
        title: "Authentication required");

    private sealed record RequestFailureRow(
        Guid Id,
        DateTime OccurredAt,
        DateTime DeadLetteredAt,
        DateTime? PublishedAt,
        int AttemptCount,
        DateTime? LockedUntil,
        string? LastErrorCode);

    private sealed record RequestJobRow(
        Guid Id,
        Guid CaseId,
        string CaseReference,
        AuditVerificationJobStatus Status,
        string? ResultId);

    private sealed record TerminalJobFailureRow(
        Guid Id,
        Guid CaseId,
        string CaseReference,
        DateTime RequestedAt,
        DateTime? CompletedAt,
        string? ErrorCode);

    private sealed record WebhookFailureRow(
        Guid Id,
        Guid VerificationJobId,
        Guid CaseId,
        string CaseReference,
        DateTime CreatedAt,
        DateTime DeadLetteredAt,
        DateTime? DeliveredAt,
        int AttemptCount,
        DateTime? LockedUntil,
        string? LastErrorCode);
}
