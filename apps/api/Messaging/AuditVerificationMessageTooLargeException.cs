using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace CaseLedger.Api.Messaging;

public sealed class AuditVerificationMessageTooLargeException(
    int maximumBytes,
    int actualBytes)
    : Exception("The audit verification snapshot exceeds the broker message limit.")
{
    public int MaximumBytes { get; } = maximumBytes;
    public int ActualBytes { get; } = actualBytes;
}

public sealed class AuditVerificationMessageTooLargeExceptionHandler
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not AuditVerificationMessageTooLargeException tooLarge)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        httpContext.Response.ContentType = "application/problem+json";
        await httpContext.Response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Status = StatusCodes.Status413PayloadTooLarge,
                Title = "Audit snapshot is too large for asynchronous verification",
                Detail =
                    "Export or verify the audit chain synchronously, or use a broker tier " +
                    "with a larger message limit.",
                Type = "https://httpstatuses.com/413",
                Extensions =
                {
                    ["maximumBytes"] = tooLarge.MaximumBytes
                }
            },
            cancellationToken);
        return true;
    }
}
