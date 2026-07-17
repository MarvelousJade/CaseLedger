using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using CaseLedger.Api.Contracts;
using CaseLedger.Api.Data;
using CaseLedger.Api.Domain;
using CaseLedger.Api.EvidenceStorage;
using CaseLedger.Api.Messaging;
using CaseLedger.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CaseLedger.Api.Endpoints;

public static class CaseEndpoints
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web);

    public static RouteGroupBuilder MapCaseEndpoints(this RouteGroupBuilder api)
    {
        var cases = api.MapGroup("/cases")
            .RequireAuthorization()
            .RequireRateLimiting("authenticated")
            .WithTags("Cases");

        cases.MapGet("", GetCasesAsync)
            .WithName("ListCases")
            .WithSummary("List and filter cases")
            .Produces<CaseCollectionResponse>()
            .Produces<HttpValidationProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status429TooManyRequests);
        cases.MapPost("", CreateCaseAsync)
            .WithName("CreateCase")
            .WithSummary("Create a case")
            .Produces<CaseDetailResponse>(StatusCodes.Status201Created)
            .Produces<HttpValidationProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status429TooManyRequests);
        cases.MapGet("/{id:guid}", GetCaseAsync)
            .WithName("GetCase")
            .WithSummary("Get a case and its current ETag")
            .Produces<CaseDetailResponse>()
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status429TooManyRequests);
        cases.MapPatch("/{id:guid}", UpdateCaseAsync)
            .WithName("UpdateCase")
            .WithSummary("Update a case when its ETag matches")
            .Accepts<UpdateCaseRequest>("application/json")
            .Produces<CaseDetailResponse>()
            .Produces<HttpValidationProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status412PreconditionFailed)
            .Produces<ProblemDetails>(StatusCodes.Status428PreconditionRequired)
            .Produces(StatusCodes.Status429TooManyRequests);
        cases.MapPost("/{id:guid}/comments", AddCommentAsync)
            .WithName("AddCaseComment")
            .WithSummary("Append a comment to a case")
            .Produces<ActivityResponse>(StatusCodes.Status201Created)
            .Produces<HttpValidationProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status429TooManyRequests);
        cases.MapPost("/{id:guid}/evidence", AddEvidenceAsync)
            .WithName("AddCaseEvidence")
            .WithSummary("Upload evidence and record its server-computed digest")
            .WithDescription(
                "Send multipart/form-data with one 'file' part to persist evidence bytes and compute SHA-256 on the server. " +
                "The application/json metadata-only request remains available for compatibility only.")
            .Accepts<AddEvidenceRequest>("application/json", "multipart/form-data")
            .Produces<EvidenceResponse>(StatusCodes.Status201Created)
            .Produces<HttpValidationProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
            .Produces<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)
            .Produces<ProblemDetails>(StatusCodes.Status415UnsupportedMediaType)
            .Produces(StatusCodes.Status429TooManyRequests);
        cases.MapGet("/{id:guid}/audit", GetAuditAsync)
            .WithName("GetCaseAudit")
            .WithSummary("Get a case's audit chain")
            .Produces<AuditCollectionResponse>()
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status429TooManyRequests);
        cases.MapGet("/{id:guid}/audit/verify", VerifyAuditAsync)
            .WithName("VerifyCaseAudit")
            .WithSummary("Verify a case's audit chain")
            .Produces<AuditVerificationResponse>()
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status429TooManyRequests);
        cases.MapPost("/{id:guid}/audit/verifications", QueueAuditVerificationAsync)
            .WithName("QueueCaseAuditVerification")
            .WithSummary("Queue an immutable audit-chain verification snapshot")
            .Produces<AuditVerificationJobResponse>(StatusCodes.Status202Accepted)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status429TooManyRequests);
        cases.MapGet("/{id:guid}/audit/verifications/latest", GetLatestAuditVerificationAsync)
            .WithName("GetLatestCaseAuditVerification")
            .WithSummary("Get the latest queued or completed audit verification")
            .Produces<AuditVerificationJobResponse>()
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status429TooManyRequests);
        cases.MapGet("/{id:guid}/audit/verifications/{jobId:guid}", GetAuditVerificationAsync)
            .WithName("GetCaseAuditVerification")
            .WithSummary("Get an audit verification job")
            .Produces<AuditVerificationJobResponse>()
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status429TooManyRequests);
        cases.MapGet("/{id:guid}/audit/export", ExportAuditAsync)
            .RequireAuthorization(policy => policy.RequireRole("Admin"))
            .WithName("ExportCaseAudit")
            .WithSummary("Export a case's audit chain (administrators only)")
            .Produces<AuditExportResponse>()
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status429TooManyRequests);

        return api;
    }

    private static async Task<IResult> GetCasesAsync(
        [AsParameters] CaseListQuery request,
        CaseLedgerDbContext db,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var query = db.Cases
            .AsNoTracking()
            .Include(item => item.Assignee)
            .Include(item => item.CreatedBy)
            .AsQueryable();

        var search = request.Search ?? request.LegacySearch;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(item =>
                item.Reference.Contains(term) ||
                item.Title.Contains(term) ||
                item.Summary.Contains(term));
        }

        var statusText = request.Status;
        if (!string.IsNullOrWhiteSpace(statusText))
        {
            if (!Enum.TryParse<CaseStatus>(statusText, true, out var status) || !Enum.IsDefined(status))
            {
                errors["status"] = ["Status must be New, InProgress, or Resolved."];
            }
            else
            {
                query = query.Where(item => item.Status == status);
            }
        }

        var severityText = request.Severity;
        if (!string.IsNullOrWhiteSpace(severityText))
        {
            if (!Enum.TryParse<CaseSeverity>(severityText, true, out var severity) || !Enum.IsDefined(severity))
            {
                errors["severity"] = ["Severity must be Low, Medium, High, or Critical."];
            }
            else
            {
                query = query.Where(item => item.Severity == severity);
            }
        }

        var usesPageParameters = request.Page.HasValue || request.PageSize.HasValue;
        var usesLegacyParameters = request.LegacyOffset.HasValue || request.LegacyLimit.HasValue;
        if (usesPageParameters && usesLegacyParameters)
        {
            errors["pagination"] = ["Use page/pageSize or the legacy offset/limit parameters, not both."];
        }

        var page = request.Page ?? 1;
        var pageSize = request.PageSize ?? 50;
        var offset = 0;
        if (usesLegacyParameters)
        {
            offset = request.LegacyOffset ?? 0;
            pageSize = request.LegacyLimit ?? 50;
            if (offset is < 0 or > 10_000)
            {
                errors["offset"] = ["offset must be between 0 and 10000."];
            }
        }
        else
        {
            if (page < 1)
            {
                errors["page"] = ["page must be 1 or greater."];
            }
        }

        if (pageSize is < 1 or > 100)
        {
            var field = usesLegacyParameters ? "limit" : "pageSize";
            errors[field] = [$"{field} must be between 1 and 100."];
        }

        if (!usesLegacyParameters && errors.Count == 0)
        {
            var calculatedOffset = (long)(page - 1) * pageSize;
            if (calculatedOffset > int.MaxValue)
            {
                errors["page"] = ["page is too large for the requested pageSize."];
            }
            else
            {
                offset = (int)calculatedOffset;
            }
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(item => item.UpdatedAt)
            .ThenBy(item => item.Reference)
            .Skip(offset)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        if (usesLegacyParameters)
        {
            page = offset / pageSize + 1;
        }

        var totalPages = total == 0
            ? 0
            : (int)Math.Ceiling(total / (double)pageSize);
        return Results.Ok(new CaseCollectionResponse(
            items.Select(CaseMappings.ToListItem).ToArray(),
            total,
            page,
            pageSize,
            totalPages,
            offset + items.Count < total,
            offset > 0));
    }

    private static async Task<IResult> GetCaseAsync(
        Guid id,
        HttpResponse httpResponse,
        CaseLedgerDbContext db,
        CancellationToken cancellationToken)
    {
        var item = await CaseMappings.LoadDetailAsync(db, id, cancellationToken);
        if (item is null)
        {
            return CaseNotFound(id);
        }

        SetETag(httpResponse, item.Version);
        return Results.Ok(item);
    }

    private static async Task<IResult> CreateCaseAsync(
        CreateCaseRequest request,
        ClaimsPrincipal principal,
        HttpResponse httpResponse,
        CaseLedgerDbContext db,
        AuditChainService auditChain,
        CaseLedgerTelemetry telemetry,
        CancellationToken cancellationToken)
    {
        var errors = ValidateCreate(request);
        if (!TryParseSeverity(request.Severity, errors, out var severity))
        {
            severity = CaseSeverity.Medium;
        }

        User? assignee = null;
        if (request.AssigneeId is not null)
        {
            assignee = await db.Users.SingleOrDefaultAsync(
                item => item.Id == request.AssigneeId.Value,
                cancellationToken);
            if (assignee is null)
            {
                errors["assigneeId"] = ["Assignee does not identify a known user."];
            }
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var actor = await ApiEndpoints.GetCurrentUserAsync(principal, db, cancellationToken);
        if (actor is null)
        {
            return AuthenticationRequired();
        }

        var now = DateTime.UtcNow;
        var tags = CaseMappings.NormalizeTags(request.Tags);
        var caseId = Guid.NewGuid();
        using var activity = telemetry.StartCaseOperation("caseledger.case.create", caseId);
        activity?.SetTag("caseledger.case.severity", severity.ToString().ToLowerInvariant());
        var caseRecord = new CaseRecord
        {
            Id = caseId,
            Reference = await NextReferenceAsync(db, now.Year, cancellationToken),
            Title = request.Title!.Trim(),
            Summary = request.Summary!.Trim(),
            Status = CaseStatus.New,
            Severity = severity,
            Category = request.Category!.Trim(),
            AssigneeId = assignee?.Id,
            Assignee = assignee,
            CreatedById = actor.Id,
            CreatedBy = actor,
            CreatedAt = now,
            UpdatedAt = now,
            DueAt = NormalizeUtc(request.DueAt),
            TagsJson = JsonSerializer.Serialize(tags)
        };

        db.Cases.Add(caseRecord);
        var auditEvent = await auditChain.AppendAsync(
            caseRecord.Id,
            "CaseCreated",
            $"Case {caseRecord.Reference} created",
            actor,
            new Dictionary<string, object?>
            {
                ["assigneeId"] = caseRecord.AssigneeId,
                ["category"] = caseRecord.Category,
                ["dueAt"] = caseRecord.DueAt,
                ["reference"] = caseRecord.Reference,
                ["severity"] = caseRecord.Severity.ToString(),
                ["status"] = caseRecord.Status.ToString(),
                ["tags"] = tags,
                ["title"] = caseRecord.Title
            },
            cancellationToken: cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
            throw;
        }

        auditChain.RecordAppendCommitted(auditEvent);
        telemetry.RecordCaseCreated(caseRecord.Id, caseRecord.Severity.ToString());
        activity?.SetStatus(ActivityStatusCode.Ok);

        var response = await CaseMappings.LoadDetailAsync(db, caseRecord.Id, cancellationToken);
        SetETag(httpResponse, caseRecord.Version);
        return Results.Created($"/api/cases/{caseRecord.Id:D}", response);
    }

    private static async Task<IResult> UpdateCaseAsync(
        Guid id,
        JsonElement body,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        ClaimsPrincipal principal,
        HttpResponse httpResponse,
        CaseLedgerDbContext db,
        AuditChainService auditChain,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return PreconditionRequired();
        }

        if (!TryParseCaseVersion(ifMatch, out var expectedVersion))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["If-Match"] = ["If-Match must contain exactly one strong quoted case version ETag."]
            });
        }

        if (body.ValueKind != JsonValueKind.Object)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["body"] = ["A JSON object is required."]
            });
        }

        UpdateCaseRequest? request;
        try
        {
            request = body.Deserialize<UpdateCaseRequest>(RequestJsonOptions);
        }
        catch (JsonException exception)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["body"] = [$"The update payload is invalid: {exception.Message}"]
            });
        }

        if (request is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["body"] = ["A JSON object is required."]
            });
        }

        var item = await db.Cases
            .Include(caseRecord => caseRecord.Assignee)
            .SingleOrDefaultAsync(caseRecord => caseRecord.Id == id, cancellationToken);
        if (item is null)
        {
            return CaseNotFound(id);
        }

        if (item.Version != expectedVersion)
        {
            SetETag(httpResponse, item.Version);
            return PreconditionFailed();
        }

        var actor = await ApiEndpoints.GetCurrentUserAsync(principal, db, cancellationToken);
        if (actor is null)
        {
            return AuthenticationRequired();
        }

        var errors = new Dictionary<string, string[]>();
        var changes = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        var recognizedFields = 0;

        if (body.TryGetProperty("title", out _))
        {
            recognizedFields++;
            if (!ValidateText(request.Title, 3, 160, "title", errors))
            {
                // Validation error is recorded above.
            }
            else
            {
                item.Title = request.Title!.Trim();
                changes["title"] = item.Title;
            }
        }

        if (body.TryGetProperty("summary", out _))
        {
            recognizedFields++;
            if (ValidateText(request.Summary, 10, 4000, "summary", errors))
            {
                item.Summary = request.Summary!.Trim();
                changes["summary"] = item.Summary;
            }
        }

        if (body.TryGetProperty("status", out _))
        {
            recognizedFields++;
            if (TryParseStatus(request.Status, errors, out var status))
            {
                item.Status = status;
                changes["status"] = status.ToString();
            }
        }

        if (body.TryGetProperty("severity", out _))
        {
            recognizedFields++;
            if (TryParseSeverity(request.Severity, errors, out var severity))
            {
                item.Severity = severity;
                changes["severity"] = severity.ToString();
            }
        }

        if (body.TryGetProperty("category", out _))
        {
            recognizedFields++;
            if (ValidateText(request.Category, 2, 100, "category", errors))
            {
                item.Category = request.Category!.Trim();
                changes["category"] = item.Category;
            }
        }

        if (body.TryGetProperty("assigneeId", out _))
        {
            recognizedFields++;
            User? assignee = null;
            if (request.AssigneeId is not null)
            {
                assignee = await db.Users.SingleOrDefaultAsync(
                    user => user.Id == request.AssigneeId.Value,
                    cancellationToken);
                if (assignee is null)
                {
                    errors["assigneeId"] = ["Assignee does not identify a known user."];
                }
            }

            if (assignee is not null || request.AssigneeId is null)
            {
                item.AssigneeId = assignee?.Id;
                item.Assignee = assignee;
                changes["assigneeId"] = assignee?.Id;
            }
        }

        if (body.TryGetProperty("dueAt", out _))
        {
            recognizedFields++;
            item.DueAt = NormalizeUtc(request.DueAt);
            changes["dueAt"] = item.DueAt;
        }

        if (body.TryGetProperty("tags", out _))
        {
            recognizedFields++;
            ValidateTags(request.Tags, errors);
            var tags = CaseMappings.NormalizeTags(request.Tags);
            item.TagsJson = JsonSerializer.Serialize(tags);
            changes["tags"] = tags;
        }

        if (recognizedFields == 0)
        {
            errors["body"] = ["Provide at least one mutable case field."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        item.UpdatedAt = DateTime.UtcNow;
        var auditEvent = await auditChain.AppendAsync(
            item.Id,
            "CaseUpdated",
            $"Case {item.Reference} updated: {string.Join(", ", changes.Keys)}",
            actor,
            changes,
            cancellationToken: cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            var currentVersion = await db.Cases
                .AsNoTracking()
                .Where(caseRecord => caseRecord.Id == id)
                .Select(caseRecord => (Guid?)caseRecord.Version)
                .SingleOrDefaultAsync(cancellationToken);
            if (currentVersion is null)
            {
                return CaseNotFound(id);
            }

            SetETag(httpResponse, currentVersion.Value);
            return PreconditionFailed();
        }

        auditChain.RecordAppendCommitted(auditEvent);
        var response = await CaseMappings.LoadDetailAsync(db, item.Id, cancellationToken);
        SetETag(httpResponse, item.Version);
        return Results.Ok(response);
    }

    private static async Task<IResult> AddCommentAsync(
        Guid id,
        AddCommentRequest request,
        ClaimsPrincipal principal,
        CaseLedgerDbContext db,
        AuditChainService auditChain,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        ValidateText(request.Body, 1, 500, "body", errors);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var item = await db.Cases.SingleOrDefaultAsync(caseRecord => caseRecord.Id == id, cancellationToken);
        if (item is null)
        {
            return CaseNotFound(id);
        }

        var actor = await ApiEndpoints.GetCurrentUserAsync(principal, db, cancellationToken);
        if (actor is null)
        {
            return AuthenticationRequired();
        }

        var body = request.Body!.Trim();
        item.UpdatedAt = DateTime.UtcNow;
        var auditEvent = await auditChain.AppendAsync(
            item.Id,
            "CommentAdded",
            body,
            actor,
            new Dictionary<string, object?> { ["body"] = body },
            cancellationToken: cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return CaseWriteConflict();
        }

        auditChain.RecordAppendCommitted(auditEvent);
        return Results.Created(
            $"/api/cases/{item.Id:D}/audit/{auditEvent.Sequence}",
            CaseMappings.ToActivity(auditEvent));
    }

    private static async Task<IResult> AddEvidenceAsync(
        Guid id,
        HttpRequest httpRequest,
        ClaimsPrincipal principal,
        CaseLedgerDbContext db,
        AuditChainService auditChain,
        CaseLedgerTelemetry telemetry,
        EvidenceUploadService uploads,
        CancellationToken cancellationToken)
    {
        AddEvidenceRequest request;
        IFormFile? uploadedFile = null;
        Dictionary<string, string[]> errors;

        if (httpRequest.HasFormContentType)
        {
            if (httpRequest.ContentLength > uploads.MultipartBodyLengthLimit)
            {
                return EvidencePayloadTooLarge(uploads.MaxFileSizeBytes);
            }

            IFormCollection form;
            try
            {
                form = await httpRequest.ReadFormAsync(cancellationToken);
            }
            catch (InvalidDataException)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["file"] = ["The multipart request body is invalid."]
                });
            }
            catch (BadHttpRequestException exception)
            {
                return Results.Problem(
                    statusCode: exception.StatusCode,
                    title: "Invalid evidence upload");
            }

            uploadedFile = form.Files.GetFile("file");
            if (uploadedFile is null || form.Files.Count != 1)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["file"] = ["Exactly one multipart file named 'file' is required."]
                });
            }

            request = new AddEvidenceRequest(
                uploadedFile.FileName,
                uploadedFile.Length,
                string.IsNullOrWhiteSpace(uploadedFile.ContentType)
                    ? "application/octet-stream"
                    : uploadedFile.ContentType.Trim(),
                null);
            if (uploadedFile.Length > uploads.MaxFileSizeBytes)
            {
                return EvidencePayloadTooLarge(uploads.MaxFileSizeBytes);
            }

            errors = ValidateEvidenceUpload(request, uploads.MaxFileSizeBytes);
        }
        else if (IsJsonContentType(httpRequest.ContentType))
        {
            try
            {
                request = await httpRequest.ReadFromJsonAsync<AddEvidenceRequest>(
                        RequestJsonOptions,
                        cancellationToken) ??
                    new AddEvidenceRequest(null, -1, null, null);
            }
            catch (JsonException)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["body"] = ["The JSON request body is invalid."]
                });
            }

            errors = ValidateEvidence(request);
        }
        else
        {
            return Results.Problem(
                statusCode: StatusCodes.Status415UnsupportedMediaType,
                title: "Unsupported media type",
                detail: "Use multipart/form-data to upload evidence bytes. " +
                        "The application/json metadata-only shape is supported for compatibility only.");
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var item = await db.Cases.SingleOrDefaultAsync(caseRecord => caseRecord.Id == id, cancellationToken);
        if (item is null)
        {
            return CaseNotFound(id);
        }

        var actor = await ApiEndpoints.GetCurrentUserAsync(principal, db, cancellationToken);
        if (actor is null)
        {
            return AuthenticationRequired();
        }

        var evidenceId = Guid.NewGuid();
        StagedEvidenceUpload? stagedUpload = null;
        if (uploadedFile is not null)
        {
            try
            {
                await using var content = uploadedFile.OpenReadStream();
                stagedUpload = await uploads.StageAsync(
                    new EvidenceObjectId(item.Id, evidenceId),
                    request.MediaType!,
                    content,
                    cancellationToken);
                request = request with
                {
                    SizeBytes = stagedUpload.SizeBytes,
                    Sha256 = stagedUpload.Sha256
                };
            }
            catch (EvidenceUploadTooLargeException exception)
            {
                return EvidencePayloadTooLarge(exception.MaxFileSizeBytes);
            }
        }

        try
        {
            var now = DateTime.UtcNow;
            var evidence = new Evidence
            {
                Id = evidenceId,
                CaseId = item.Id,
                Case = item,
                FileName = request.FileName!.Trim(),
                SizeBytes = request.SizeBytes,
                MediaType = request.MediaType!.Trim(),
                Sha256 = request.Sha256!.Trim().ToLowerInvariant(),
                AddedById = actor.Id,
                AddedBy = actor,
                CreatedAt = now
            };
            using var activity = telemetry.StartCaseOperation(
                "caseledger.evidence.register",
                item.Id);
            activity?.SetTag("caseledger.evidence.id", evidence.Id.ToString("D"));
            activity?.SetTag("caseledger.evidence.size_bytes", evidence.SizeBytes);

            db.Evidence.Add(evidence);
            item.UpdatedAt = now;
            var auditEvent = await auditChain.AppendAsync(
                item.Id,
                "EvidenceAdded",
                $"Evidence {evidence.FileName} added",
                actor,
                new Dictionary<string, object?>
                {
                    ["evidenceId"] = evidence.Id,
                    ["fileName"] = evidence.FileName,
                    ["mediaType"] = evidence.MediaType,
                    ["sha256"] = evidence.Sha256,
                    ["sizeBytes"] = evidence.SizeBytes
                },
                cancellationToken: cancellationToken);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "concurrency_conflict");
                return CaseWriteConflict();
            }
            catch (Exception exception)
            {
                activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
                throw;
            }

            stagedUpload?.Complete();
            auditChain.RecordAppendCommitted(auditEvent);
            telemetry.RecordEvidenceRegistered(
                item.Id,
                evidence.Id,
                evidence.MediaType,
                evidence.SizeBytes);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return Results.Created(
                $"/api/cases/{item.Id:D}/evidence/{evidence.Id:D}",
                CaseMappings.ToEvidence(evidence));
        }
        finally
        {
            if (stagedUpload is not null)
            {
                await stagedUpload.DisposeAsync();
            }
        }
    }

    private static async Task<IResult> GetAuditAsync(
        Guid id,
        CaseLedgerDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await db.Cases.AsNoTracking().AnyAsync(item => item.Id == id, cancellationToken))
        {
            return CaseNotFound(id);
        }

        var events = await db.AuditEvents
            .AsNoTracking()
            .Where(item => item.CaseId == id)
            .OrderBy(item => item.Sequence)
            .ToListAsync(cancellationToken);
        var response = events.Select(item => new AuditEventResponse(
            item.Id,
            item.Sequence,
            item.EventType,
            item.Description,
            item.ActorName,
            item.CreatedAt,
            item.PreviousHash,
            item.Hash,
            item.CanonicalData)).ToArray();

        return Results.Ok(new AuditCollectionResponse(response, response.Length));
    }

    private static async Task<IResult> VerifyAuditAsync(
        Guid id,
        CaseLedgerDbContext db,
        AuditChainService auditChain,
        CancellationToken cancellationToken)
    {
        if (!await db.Cases.AsNoTracking().AnyAsync(item => item.Id == id, cancellationToken))
        {
            return CaseNotFound(id);
        }

        return Results.Ok(await auditChain.VerifyAsync(id, cancellationToken));
    }

    private static async Task<IResult> QueueAuditVerificationAsync(
        Guid id,
        HttpContext httpContext,
        CaseLedgerDbContext db,
        AuditVerificationScheduler scheduler,
        CancellationToken cancellationToken)
    {
        var caseRecord = await db.Cases.SingleOrDefaultAsync(
            item => item.Id == id,
            cancellationToken);
        if (caseRecord is null)
        {
            return CaseNotFound(id);
        }

        if (!scheduler.IsEnabled)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Verification messaging is unavailable");
        }

        var job = await scheduler.ScheduleAsync(
            id,
            httpContext.TraceIdentifier,
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        var location = $"/api/cases/{id:D}/audit/verifications/{job.Id:D}";
        return Results.Accepted(location, ToVerificationJobResponse(job, caseRecord));
    }

    private static async Task<IResult> GetAuditVerificationAsync(
        Guid id,
        Guid jobId,
        CaseLedgerDbContext db,
        CancellationToken cancellationToken)
    {
        var caseHead = await db.Cases
            .AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new { item.AuditHeadSequence, item.AuditHeadHash })
            .SingleOrDefaultAsync(cancellationToken);
        if (caseHead is null)
        {
            return CaseNotFound(id);
        }

        var job = await db.AuditVerificationJobs
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == jobId && item.CaseId == id,
                cancellationToken);
        return job is null
            ? Results.NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Verification job not found"
            })
            : Results.Ok(ToVerificationJobResponse(
                job,
                caseHead.AuditHeadSequence,
                caseHead.AuditHeadHash));
    }

    private static async Task<IResult> GetLatestAuditVerificationAsync(
        Guid id,
        CaseLedgerDbContext db,
        CancellationToken cancellationToken)
    {
        var caseHead = await db.Cases
            .AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new { item.AuditHeadSequence, item.AuditHeadHash })
            .SingleOrDefaultAsync(cancellationToken);
        if (caseHead is null)
        {
            return CaseNotFound(id);
        }

        var job = await db.AuditVerificationJobs
            .AsNoTracking()
            .Where(item => item.CaseId == id)
            .OrderByDescending(item => item.RequestedAt)
            .ThenByDescending(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return job is null
            ? Results.NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Verification job not found"
            })
            : Results.Ok(ToVerificationJobResponse(
                job,
                caseHead.AuditHeadSequence,
                caseHead.AuditHeadHash));
    }

    private static async Task<IResult> ExportAuditAsync(
        Guid id,
        HttpResponse httpResponse,
        CaseLedgerDbContext db,
        CancellationToken cancellationToken)
    {
        var caseRecord = await db.Cases
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (caseRecord is null)
        {
            return CaseNotFound(id);
        }

        var events = await db.AuditEvents
            .AsNoTracking()
            .Where(item => item.CaseId == id)
            .OrderBy(item => item.Sequence)
            .Select(item => new AuditExportEventResponse(
                item.Sequence,
                item.PreviousHash,
                item.Hash,
                item.CanonicalData))
            .ToListAsync(cancellationToken);

        httpResponse.Headers.ContentDisposition =
            $"attachment; filename=\"{caseRecord.Reference.ToLowerInvariant()}-audit.json\"";
        return Results.Ok(new AuditExportResponse(
            caseRecord.Id,
            caseRecord.Reference,
            DateTime.UtcNow,
            events));
    }

    private static AuditVerificationJobResponse ToVerificationJobResponse(
        AuditVerificationJob job,
        CaseRecord caseRecord) =>
        ToVerificationJobResponse(
            job,
            caseRecord.AuditHeadSequence,
            caseRecord.AuditHeadHash);

    private static AuditVerificationJobResponse ToVerificationJobResponse(
        AuditVerificationJob job,
        int currentSequence,
        string currentHash) =>
        new(
            job.Id,
            job.Status.ToString(),
            job.TargetSequence,
            job.TargetHash,
            job.ResultId,
            job.Valid,
            job.CheckedEvents,
            job.BrokenAt,
            job.ChainHead,
            job.SnapshotSha256,
            job.ErrorCode,
            job.RequestedAt,
            job.CompletedAt,
            job.TargetSequence == currentSequence &&
            string.Equals(job.TargetHash, currentHash, StringComparison.Ordinal));

    private static Dictionary<string, string[]> ValidateCreate(CreateCaseRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        ValidateText(request.Title, 3, 160, "title", errors);
        ValidateText(request.Summary, 10, 4000, "summary", errors);
        ValidateText(request.Category, 2, 100, "category", errors);
        ValidateTags(request.Tags, errors);
        return errors;
    }

    private static Dictionary<string, string[]> ValidateEvidence(AddEvidenceRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        ValidateText(request.FileName, 1, 255, "fileName", errors);
        ValidateText(request.MediaType, 3, 150, "mediaType", errors);
        if (request.SizeBytes < 0)
        {
            errors["sizeBytes"] = ["Size must be zero or greater."];
        }

        var sha256 = request.Sha256?.Trim();
        if (sha256 is null || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
        {
            errors["sha256"] = ["SHA-256 must be exactly 64 hexadecimal characters."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateEvidenceUpload(
        AddEvidenceRequest request,
        long maxFileSizeBytes)
    {
        var errors = new Dictionary<string, string[]>();
        ValidateText(request.FileName, 1, 255, "fileName", errors);
        ValidateText(request.MediaType, 3, 150, "mediaType", errors);

        var fileName = request.FileName?.Trim();
        if (fileName is not null &&
            (fileName is "." or ".." ||
             fileName.IndexOfAny(['/', '\\']) >= 0 ||
             fileName.Any(char.IsControl)))
        {
            errors["fileName"] = ["File name must be a plain name without path separators or control characters."];
        }

        if (request.SizeBytes < 0)
        {
            errors["sizeBytes"] = ["Size must be zero or greater."];
        }
        else if (request.SizeBytes > maxFileSizeBytes)
        {
            errors["sizeBytes"] = [$"Size must not exceed {maxFileSizeBytes} bytes."];
        }

        if (!MediaTypeHeaderValue.TryParse(request.MediaType, out var mediaType) ||
            string.IsNullOrWhiteSpace(mediaType.MediaType))
        {
            errors["mediaType"] = ["Media type must be a valid Internet media type."];
        }

        return errors;
    }

    private static bool IsJsonContentType(string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var parsed) ||
            parsed.MediaType is null)
        {
            return false;
        }

        return parsed.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
               parsed.MediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    private static IResult EvidencePayloadTooLarge(long maxFileSizeBytes) =>
        Results.Problem(
            statusCode: StatusCodes.Status413PayloadTooLarge,
            title: "Evidence file is too large",
            detail: $"The maximum evidence file size is {maxFileSizeBytes} bytes.");

    private static bool ValidateText(
        string? value,
        int minimumLength,
        int maximumLength,
        string field,
        IDictionary<string, string[]> errors)
    {
        var length = value?.Trim().Length ?? 0;
        if (length < minimumLength || length > maximumLength)
        {
            errors[field] = [$"{field} must contain between {minimumLength} and {maximumLength} characters."];
            return false;
        }

        return true;
    }

    private static void ValidateTags(
        IReadOnlyList<string>? tags,
        IDictionary<string, string[]> errors)
    {
        if (tags is null)
        {
            return;
        }

        if (tags.Count > 10)
        {
            errors["tags"] = ["A case can have at most 10 tags."];
        }
        else if (tags.Any(tag => tag is null || tag.Trim().Length is 0 or > 40))
        {
            errors["tags"] = ["Each tag must contain between 1 and 40 characters."];
        }
    }

    private static bool TryParseStatus(
        string? value,
        IDictionary<string, string[]> errors,
        out CaseStatus status)
    {
        if (!Enum.TryParse(value, true, out status) || !Enum.IsDefined(status))
        {
            errors["status"] = ["Status must be New, InProgress, or Resolved."];
            return false;
        }

        return true;
    }

    private static bool TryParseSeverity(
        string? value,
        IDictionary<string, string[]> errors,
        out CaseSeverity severity)
    {
        if (!Enum.TryParse(value, true, out severity) || !Enum.IsDefined(severity))
        {
            errors["severity"] = ["Severity must be Low, Medium, High, or Critical."];
            return false;
        }

        return true;
    }

    private static async Task<string> NextReferenceAsync(
        CaseLedgerDbContext db,
        int year,
        CancellationToken cancellationToken)
    {
        var prefix = $"CL-{year}-";
        var references = await db.Cases
            .AsNoTracking()
            .Where(item => item.Reference.StartsWith(prefix))
            .Select(item => item.Reference)
            .ToListAsync(cancellationToken);

        var highest = references
            .Select(reference => int.TryParse(reference[prefix.Length..], out var number) ? number : 0)
            .DefaultIfEmpty()
            .Max();
        return $"{prefix}{highest + 1:D3}";
    }

    private static DateTime? NormalizeUtc(DateTime? value)
    {
        if (value is null)
        {
            return null;
        }

        return value.Value.Kind switch
        {
            DateTimeKind.Utc => value.Value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
        };
    }

    private static IResult CaseNotFound(Guid id) => Results.Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "Case not found",
        detail: $"No case with id '{id:D}' exists.");

    private static IResult AuthenticationRequired() => Results.Problem(
        statusCode: StatusCodes.Status401Unauthorized,
        title: "Authentication required");

    private static void SetETag(HttpResponse response, Guid version) =>
        response.Headers.ETag = $"\"{version:D}\"";

    private static bool TryParseCaseVersion(string value, out Guid version)
    {
        version = default;
        var candidate = value.Trim();
        return candidate.Length == 38 &&
               candidate[0] == '"' &&
               candidate[^1] == '"' &&
               Guid.TryParseExact(candidate[1..^1], "D", out version);
    }

    private static IResult PreconditionRequired() => Results.Problem(
        statusCode: StatusCodes.Status428PreconditionRequired,
        title: "If-Match is required",
        detail: "Read the case, then send its quoted ETag in the If-Match header.");

    private static IResult PreconditionFailed() => Results.Problem(
        statusCode: StatusCodes.Status412PreconditionFailed,
        title: "Case version is stale",
        detail: "The case changed after it was read. Fetch the latest representation and retry.");

    private static IResult CaseWriteConflict() => Results.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Case changed concurrently",
        detail: "The case changed while the command was being saved. Fetch the latest case and retry.");
}
