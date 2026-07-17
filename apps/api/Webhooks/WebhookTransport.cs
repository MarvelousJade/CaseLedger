using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace CaseLedger.Api.Webhooks;

public sealed record WebhookDispatchRequest(
    Guid DeliveryId,
    string EventType,
    string PayloadJson,
    string Timestamp,
    string Signature);

public sealed record WebhookTransportResponse(int StatusCode);

public interface IWebhookTransport
{
    Task<WebhookTransportResponse> PostAsync(
        WebhookDispatchRequest request,
        CancellationToken cancellationToken);
}

public sealed class WebhookTransportException(
    string errorCode,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string ErrorCode { get; } = errorCode;
}

public static class WebhookSignature
{
    public static string Create(
        string signingSecret,
        string timestamp,
        string rawBody)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingSecret));
        var prefix = Encoding.UTF8.GetBytes(timestamp + ".");
        var body = Encoding.UTF8.GetBytes(rawBody);
        hmac.TransformBlock(prefix, 0, prefix.Length, null, 0);
        hmac.TransformFinalBlock(body, 0, body.Length);
        return "v1=" + Convert.ToHexString(hmac.Hash!).ToLowerInvariant();
    }
}

public sealed class HttpWebhookTransport(
    IHttpClientFactory httpClientFactory,
    IOptions<WebhookOptions> options)
    : IWebhookTransport
{
    public async Task<WebhookTransportResponse> PostAsync(
        WebhookDispatchRequest request,
        CancellationToken cancellationToken)
    {
        var destination = WebhookRuntimeOptions.Validate(options.Value);
        using var message = new HttpRequestMessage(HttpMethod.Post, destination);
        message.Headers.TryAddWithoutValidation(
            "X-CaseLedger-Delivery",
            request.DeliveryId.ToString("D"));
        message.Headers.TryAddWithoutValidation(
            "X-CaseLedger-Event",
            request.EventType);
        message.Headers.TryAddWithoutValidation(
            "X-CaseLedger-Timestamp",
            request.Timestamp);
        message.Headers.TryAddWithoutValidation(
            "X-CaseLedger-Signature",
            request.Signature);
        message.Headers.TryAddWithoutValidation(
            "Idempotency-Key",
            request.DeliveryId.ToString("D"));
        message.Content = new ByteArrayContent(
            Encoding.UTF8.GetBytes(request.PayloadJson));
        message.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8"
            };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
        try
        {
            using var response = await httpClientFactory
                .CreateClient("CaseLedger.Webhook")
                .SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token);
            return new WebhookTransportResponse((int)response.StatusCode);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new WebhookTransportException(
                "WEBHOOK_TIMEOUT",
                "The webhook request timed out.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new WebhookTransportException(
                "WEBHOOK_NETWORK_ERROR",
                "The webhook request failed before receiving a response.",
                exception);
        }
    }
}
