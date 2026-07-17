using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CaseLedger.Api.Services;

public sealed class CaseLedgerTelemetry : IDisposable
{
    public const string InstrumentationName = "CaseLedger.Api";

    private readonly ActivitySource activitySource = new(InstrumentationName);
    private readonly Meter meter = new(InstrumentationName);
    private readonly Counter<long> casesCreated;
    private readonly Counter<long> evidenceRegistered;
    private readonly Counter<long> auditEventsAppended;
    private readonly Counter<long> auditVerifications;
    private readonly Histogram<double> auditVerificationDuration;
    private readonly ILogger<CaseLedgerTelemetry> logger;

    public CaseLedgerTelemetry(ILogger<CaseLedgerTelemetry> logger)
    {
        this.logger = logger;
        casesCreated = meter.CreateCounter<long>(
            "caseledger.cases.created",
            description: "Number of cases created.");
        evidenceRegistered = meter.CreateCounter<long>(
            "caseledger.evidence.registered",
            description: "Number of evidence fingerprints registered.");
        auditEventsAppended = meter.CreateCounter<long>(
            "caseledger.audit.events.appended",
            description: "Number of audit events committed.");
        auditVerifications = meter.CreateCounter<long>(
            "caseledger.audit.verifications",
            description: "Number of audit-chain verification attempts.");
        auditVerificationDuration = meter.CreateHistogram<double>(
            "caseledger.audit.verification.duration",
            unit: "s",
            description: "Audit-chain verification duration in seconds.");
    }

    public Activity? StartCaseOperation(string operation, Guid caseId)
    {
        var activity = activitySource.StartActivity(operation, ActivityKind.Internal);
        activity?.SetTag("caseledger.case.id", caseId.ToString("D"));
        return activity;
    }

    public void RecordCaseCreated(Guid caseId, string severity)
    {
        var normalizedSeverity = NormalizeSeverity(severity);
        casesCreated.Add(
            1,
            new KeyValuePair<string, object?>("caseledger.case.severity", normalizedSeverity));
        logger.LogInformation(
            "Case {CaseId} created with severity {Severity}.",
            caseId,
            normalizedSeverity);
    }

    public void RecordEvidenceRegistered(
        Guid caseId,
        Guid evidenceId,
        string mediaType,
        long sizeBytes)
    {
        var mediaTypeGroup = NormalizeMediaTypeGroup(mediaType);
        evidenceRegistered.Add(
            1,
            new KeyValuePair<string, object?>("caseledger.evidence.media_type_group", mediaTypeGroup));
        logger.LogInformation(
            "Evidence {EvidenceId} registered for case {CaseId} with media type group {MediaTypeGroup} and size {SizeBytes} bytes.",
            evidenceId,
            caseId,
            mediaTypeGroup,
            sizeBytes);
    }

    public void RecordAuditEventAppended(Guid caseId, int sequence, string eventType)
    {
        var normalizedEventType = NormalizeAuditEventType(eventType);
        using var activity = StartCaseOperation("caseledger.audit.append", caseId);
        activity?.SetTag("caseledger.audit.sequence", sequence);
        activity?.SetTag("caseledger.audit.event_type", normalizedEventType);
        auditEventsAppended.Add(
            1,
            new KeyValuePair<string, object?>("caseledger.audit.event_type", normalizedEventType));
        logger.LogInformation(
            "Audit event committed for case {CaseId} at sequence {Sequence} with type {EventType}.",
            caseId,
            sequence,
            normalizedEventType);
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    public void RecordAuditVerification(
        Guid caseId,
        bool valid,
        int checkedEvents,
        int? brokenAt,
        TimeSpan elapsed)
    {
        var result = valid ? "valid" : "invalid";
        auditVerifications.Add(
            1,
            new KeyValuePair<string, object?>("caseledger.audit.result", result));
        auditVerificationDuration.Record(
            elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("caseledger.audit.result", result));

        if (valid)
        {
            logger.LogInformation(
                "Audit verification completed for case {CaseId}: {CheckedEvents} events valid in {ElapsedMilliseconds} ms.",
                caseId,
                checkedEvents,
                elapsed.TotalMilliseconds);
        }
        else
        {
            logger.LogWarning(
                "Audit verification failed for case {CaseId} after {CheckedEvents} events at sequence {BrokenAt} in {ElapsedMilliseconds} ms.",
                caseId,
                checkedEvents,
                brokenAt,
                elapsed.TotalMilliseconds);
        }
    }

    private static string NormalizeSeverity(string severity) => severity switch
    {
        "Low" => "low",
        "Medium" => "medium",
        "High" => "high",
        "Critical" => "critical",
        _ => "unknown"
    };

    private static string NormalizeMediaTypeGroup(string mediaType)
    {
        var separator = mediaType.IndexOf('/');
        var group = separator > 0
            ? mediaType[..separator].Trim().ToLowerInvariant()
            : string.Empty;
        return group is "application" or "audio" or "image" or "text" or "video"
            ? group
            : "other";
    }

    private static string NormalizeAuditEventType(string eventType) => eventType switch
    {
        "CaseCreated" => "case_created",
        "CaseUpdated" => "case_updated",
        "CommentAdded" => "comment_added",
        "EvidenceAdded" => "evidence_added",
        _ => "other"
    };

    public void Dispose()
    {
        activitySource.Dispose();
        meter.Dispose();
    }
}
