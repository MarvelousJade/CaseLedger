using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CaseLedger.Api.Messaging;

public sealed record AuditVerificationResultV1(
    int SchemaVersion,
    string MessageType,
    string ResultId,
    Guid JobId,
    string CorrelationId,
    Guid CaseId,
    string VerificationProfile,
    string SnapshotSha256,
    DateTimeOffset CompletedAt,
    string Outcome,
    int CheckedEvents,
    string? ChainHead,
    int? BrokenAt,
    AuditVerificationResultErrorV1? Error,
    int Attempt,
    string WorkerVersion);

public sealed record AuditVerificationResultErrorV1(
    string Code,
    string Message,
    bool Retryable);

public sealed class ResultMessageValidationException(
    string errorCode,
    string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}

public static partial class AuditVerificationResultContract
{
    public const string MessageType = "caseledger.audit.verification.result.v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static AuditVerificationResultV1 Parse(ReadOnlyMemory<byte> payload)
    {
        AuditVerificationResultV1? result;
        try
        {
            result = JsonSerializer.Deserialize<AuditVerificationResultV1>(
                payload.Span,
                JsonOptions);
        }
        catch (JsonException)
        {
            throw new ResultMessageValidationException(
                "INVALID_RESULT_JSON",
                "The verification result is not valid contract JSON.");
        }

        if (result is null)
        {
            throw new ResultMessageValidationException(
                "INVALID_RESULT_JSON",
                "The verification result body is empty.");
        }

        Validate(result);
        return result;
    }

    public static void Validate(AuditVerificationResultV1 result)
    {
        if (result.SchemaVersion != 1 ||
            !string.Equals(result.MessageType, MessageType, StringComparison.Ordinal))
        {
            throw Invalid("RESULT_SCHEMA_INVALID");
        }

        if (!string.Equals(
                result.ResultId,
                ResultIdFor(result.JobId),
                StringComparison.Ordinal))
        {
            throw Invalid("RESULT_ID_INVALID");
        }

        if (result.JobId == Guid.Empty ||
            result.CaseId == Guid.Empty ||
            result.CompletedAt == default ||
            string.IsNullOrWhiteSpace(result.CorrelationId) ||
            result.CorrelationId.Length > 128 ||
            !string.Equals(
                result.VerificationProfile,
                AuditVerificationScheduler.VerificationProfile,
                StringComparison.Ordinal) ||
            string.IsNullOrEmpty(result.SnapshotSha256) ||
            !Sha256Regex().IsMatch(result.SnapshotSha256) ||
            (result.ChainHead is not null && !Sha256Regex().IsMatch(result.ChainHead)) ||
            result.CheckedEvents < 0 ||
            result.BrokenAt is < 1 ||
            result.Attempt < 0 ||
            string.IsNullOrWhiteSpace(result.WorkerVersion) ||
            result.WorkerVersion.Length > 80)
        {
            throw Invalid("RESULT_SCHEMA_INVALID");
        }

        if (result.Error is not null &&
            (result.Error.Retryable ||
             string.IsNullOrWhiteSpace(result.Error.Code) ||
             result.Error.Code.Length > 80 ||
             string.IsNullOrWhiteSpace(result.Error.Message)))
        {
            throw Invalid("RESULT_SCHEMA_INVALID");
        }

        switch (result.Outcome)
        {
            case "valid" when
                result.Error is null &&
                result.BrokenAt is null &&
                result.ChainHead is not null:
            case "invalid" when
                result.Error is not null &&
                result.ChainHead is null:
                break;
            case "error" when
                result.Error is not null &&
                result.ChainHead is null:
                break;
            default:
                throw Invalid("RESULT_SCHEMA_INVALID");
        }
    }

    public static string ResultIdFor(Guid jobId) =>
        $"audit-verification:{jobId:D}:v1";

    private static ResultMessageValidationException Invalid(string code) =>
        new(code, "The verification result does not satisfy the v1 contract.");

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();
}
