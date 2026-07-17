using System.Globalization;
using System.Text.Json;
using CaseLedger.Api.Domain;

namespace CaseLedger.Api.Webhooks;

public static class VerificationWebhookV1
{
    public const string EventType =
        "caseledger.audit.verification.completed.v1";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static WebhookDelivery CreateDelivery(
        AuditVerificationJob job,
        DateTime createdAt)
    {
        var deliveryId = Guid.NewGuid();
        var payload = new VerificationWebhookPayloadV1(
            deliveryId,
            job.ResultId!,
            job.Id,
            job.CaseId,
            job.Status.ToString(),
            job.Valid,
            job.CheckedEvents,
            job.BrokenAt,
            job.ChainHead,
            job.ErrorCode,
            job.CompletedAt!.Value.ToString(
                "O",
                CultureInfo.InvariantCulture));

        return new WebhookDelivery
        {
            Id = deliveryId,
            VerificationJobId = job.Id,
            VerificationJob = job,
            ResultId = job.ResultId!,
            EventType = EventType,
            PayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
            CreatedAt = createdAt,
            NextAttemptAt = createdAt
        };
    }
}

public sealed record VerificationWebhookPayloadV1(
    Guid DeliveryId,
    string ResultId,
    Guid JobId,
    Guid CaseId,
    string Status,
    bool? Valid,
    int? CheckedEvents,
    int? BrokenAt,
    string? ChainHead,
    string? ErrorCode,
    string CompletedAt);
