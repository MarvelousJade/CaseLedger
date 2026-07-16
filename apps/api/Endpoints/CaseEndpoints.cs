using System.Security.Claims;
using System.Text.Json;
using CaseLedger.Api.Contracts;
using CaseLedger.Api.Data;
using CaseLedger.Api.Domain;
using CaseLedger.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CaseLedger.Api.Endpoints;

public static class CaseEndpoints
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web);

    public static RouteGroupBuilder MapCaseEndpoints(this RouteGroupBuilder api)
    {
        var cases = api.MapGroup("/cases").RequireAuthorization();

        cases.MapGet("", GetCasesAsync);
        cases.MapPost("", CreateCaseAsync);
        cases.MapGet("/{id:guid}", GetCaseAsync);
        cases.MapPatch("/{id:guid}", UpdateCaseAsync);
        cases.MapPost("/{id:guid}/comments", AddCommentAsync);
        cases.MapPost("/{id:guid}/evidence", AddEvidenceAsync);
        cases.MapGet("/{id:guid}/audit", GetAuditAsync);
        cases.MapGet("/{id:guid}/audit/verify", VerifyAuditAsync);
        cases.MapGet("/{id:guid}/audit/export", ExportAuditAsync)
            .RequireAuthorization(policy => policy.RequireRole("Admin"));

        return api;
    }

    private static async Task<IResult> GetCasesAsync(
        HttpRequest request,
        CaseLedgerDbContext db,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var query = db.Cases
            .AsNoTracking()
            .Include(item => item.Assignee)
            .Include(item => item.CreatedBy)
            .AsQueryable();

        var search = request.Query["search"].FirstOrDefault() ?? request.Query["q"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(item =>
                item.Reference.Contains(term) ||
                item.Title.Contains(term) ||
                item.Summary.Contains(term));
        }

        var statusText = request.Query["status"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(statusText))
        {
            if (!Enum.TryParse<CaseStatus>(statusText, true, out var status))
            {
                errors["status"] = ["Status must be New, InProgress, or Resolved."];
            }
            else
            {
                query = query.Where(item => item.Status == status);
            }
        }

        var severityText = request.Query["severity"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(severityText))
        {
            if (!Enum.TryParse<CaseSeverity>(severityText, true, out var severity))
            {
                errors["severity"] = ["Severity must be Low, Medium, High, or Critical."];
            }
            else
            {
                query = query.Where(item => item.Severity == severity);
            }
        }

        var offset = ParseBoundedInteger(request.Query["offset"].FirstOrDefault(), 0, 0, 10_000, "offset", errors);
        var limit = ParseBoundedInteger(request.Query["limit"].FirstOrDefault(), 50, 1, 100, "limit", errors);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(item => item.UpdatedAt)
            .ThenBy(item => item.Reference)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return Results.Ok(new CaseCollectionResponse(
            items.Select(CaseMappings.ToListItem).ToArray(),
            total));
    }

    private static async Task<IResult> GetCaseAsync(
        Guid id,
        CaseLedgerDbContext db,
        CancellationToken cancellationToken)
    {
        var item = await CaseMappings.LoadDetailAsync(db, id, cancellationToken);
        return item is null ? CaseNotFound(id) : Results.Ok(item);
    }

    private static async Task<IResult> CreateCaseAsync(
        CreateCaseRequest request,
        ClaimsPrincipal principal,
        CaseLedgerDbContext db,
        AuditChainService auditChain,
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
        var caseRecord = new CaseRecord
        {
            Id = Guid.NewGuid(),
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
        await auditChain.AppendAsync(
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
        await db.SaveChangesAsync(cancellationToken);

        var response = await CaseMappings.LoadDetailAsync(db, caseRecord.Id, cancellationToken);
        return Results.Created($"/api/cases/{caseRecord.Id:D}", response);
    }

    private static async Task<IResult> UpdateCaseAsync(
        Guid id,
        JsonElement body,
        ClaimsPrincipal principal,
        CaseLedgerDbContext db,
        AuditChainService auditChain,
        CancellationToken cancellationToken)
    {
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
        await auditChain.AppendAsync(
            item.Id,
            "CaseUpdated",
            $"Case {item.Reference} updated: {string.Join(", ", changes.Keys)}",
            actor,
            changes,
            cancellationToken: cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        var response = await CaseMappings.LoadDetailAsync(db, item.Id, cancellationToken);
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
        await db.SaveChangesAsync(cancellationToken);

        return Results.Created(
            $"/api/cases/{item.Id:D}/audit/{auditEvent.Sequence}",
            CaseMappings.ToActivity(auditEvent));
    }

    private static async Task<IResult> AddEvidenceAsync(
        Guid id,
        AddEvidenceRequest request,
        ClaimsPrincipal principal,
        CaseLedgerDbContext db,
        AuditChainService auditChain,
        CancellationToken cancellationToken)
    {
        var errors = ValidateEvidence(request);
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

        var now = DateTime.UtcNow;
        var evidence = new Evidence
        {
            Id = Guid.NewGuid(),
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

        db.Evidence.Add(evidence);
        item.UpdatedAt = now;
        await auditChain.AppendAsync(
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
        await db.SaveChangesAsync(cancellationToken);

        return Results.Created(
            $"/api/cases/{item.Id:D}/evidence/{evidence.Id:D}",
            CaseMappings.ToEvidence(evidence));
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

    private static int ParseBoundedInteger(
        string? value,
        int defaultValue,
        int minimum,
        int maximum,
        string field,
        IDictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!int.TryParse(value, out var parsed) || parsed < minimum || parsed > maximum)
        {
            errors[field] = [$"{field} must be between {minimum} and {maximum}."];
            return defaultValue;
        }

        return parsed;
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
}
