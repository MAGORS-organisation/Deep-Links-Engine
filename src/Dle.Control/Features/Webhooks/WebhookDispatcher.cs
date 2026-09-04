using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

using Dle.Domain.Abuse;
using Dle.Domain.Crypto;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dle.Control.Features.Webhooks;

/// <summary>
/// The result of one delivery attempt.
/// </summary>
/// <param name="Outcome">What happened: <c>delivered</c>, <c>failed</c> or <c>unsendable</c>.</param>
/// <param name="StatusCode">HTTP status returned, when the endpoint answered.</param>
/// <param name="Error">Short description of the failure. Never the request headers, which carry
/// the signature, and never the response body, which is somebody else's content.</param>
/// <param name="Elapsed">Wall clock time of the attempt.</param>
/// <param name="Retryable">Whether trying again could plausibly succeed.</param>
public sealed record WebhookAttempt(
    string Outcome,
    int? StatusCode,
    string? Error,
    TimeSpan Elapsed,
    bool Retryable)
{
    /// <summary>Outcome of an attempt the endpoint accepted.</summary>
    public const string Delivered = "delivered";

    /// <summary>Outcome of an attempt that failed and may be retried.</summary>
    public const string Failed = "failed";

    /// <summary>Outcome of a delivery that cannot be sent at all, whatever the endpoint does.</summary>
    public const string Unsendable = "unsendable";

    /// <summary>Whether the endpoint accepted the delivery.</summary>
    public bool Succeeded => string.Equals(Outcome, Delivered, StringComparison.Ordinal);
}

/// <summary>
/// Signs one webhook payload and posts it (§B.7.4, FR-204, T-02, T-13, TC-165).
/// </summary>
/// <remarks>
/// <para>
/// One class rather than logic inside the worker, because the same delivery is produced by two
/// callers: the dispatcher draining the outbox and the <c>/webhooks/{id}/test</c> endpoint a
/// customer presses while integrating. They must produce byte-identical requests, or the test
/// button proves nothing about the real deliveries.
/// </para>
/// <para>
/// The bytes are the contract. The payload is rendered once, when the event is recorded, stored as
/// text, and posted here without re-serialization; the signature is computed over exactly those
/// bytes plus the timestamp. A retry therefore sends the same body and the same signature material
/// as the first attempt, and a body altered anywhere in between fails verification (TC-165).
/// </para>
/// <para>
/// The destination is validated twice. Once when the subscription is created, and once here in the
/// connect callback of the handler, where the address that is checked is the very address the
/// socket connects to. Only the second one is proof against DNS rebinding: between a check on the
/// resolved address and the connection, the resolver can answer differently, and a webhook aimed
/// at <c>169.254.169.254</c> would turn the control plane into a proxy for the cloud metadata
/// service (T-02).
/// </para>
/// </remarks>
public sealed partial class WebhookDispatcher
{
    /// <summary>Name of the outbound client, whose handler enforces the destination policy.</summary>
    public const string HttpClientName = "dle-webhooks";

    /// <summary>Media type every delivery is sent as.</summary>
    private const string ContentType = "application/json";

    /// <summary>Longest error text kept on a delivery row.</summary>
    private const int MaxErrorLength = 500;

    private readonly IHttpClientFactory _clients;
    private readonly WebhookSigningKeys _keys;
    private readonly WebhookSecretProtector _secrets;
    private readonly WebhookMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IOptionsMonitor<WebhookOptions> _options;
    private readonly ILogger<WebhookDispatcher> _logger;

    /// <summary>
    /// Creates the dispatcher.
    /// </summary>
    /// <param name="clients">Factory of the guarded outbound client.</param>
    /// <param name="keys">Webhook signing keys.</param>
    /// <param name="secrets">Protector of the per subscription shared secret.</param>
    /// <param name="metrics">Instruments (§C.6).</param>
    /// <param name="timeProvider">Clock (SHARED-KERNEL §17.2).</param>
    /// <param name="options">Webhook options.</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WebhookDispatcher(
        IHttpClientFactory clients,
        WebhookSigningKeys keys,
        WebhookSecretProtector secrets,
        WebhookMetrics metrics,
        TimeProvider timeProvider,
        IOptionsMonitor<WebhookOptions> options,
        ILogger<WebhookDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _clients = clients;
        _keys = keys;
        _secrets = secrets;
        _metrics = metrics;
        _timeProvider = timeProvider;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Signs and posts one payload.
    /// </summary>
    /// <param name="url">Destination URL of the subscription.</param>
    /// <param name="secretEncrypted">The subscription's secret, as stored.</param>
    /// <param name="eventType">Event type, used for the instruments and for the header.</param>
    /// <param name="payloadJson">The exact body to send, as it was rendered when the event was
    /// recorded.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened.</returns>
    /// <exception cref="ArgumentException">A required argument is empty.</exception>
    public async Task<WebhookAttempt> SendAsync(
        string url,
        byte[]? secretEncrypted,
        string eventType,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);

        long started = Stopwatch.GetTimestamp();

        UrlSafetyVerdict verdict = TargetUrlPolicy.ValidateSyntax(url);

        if (verdict.Level != UrlSafetyLevel.Safe)
        {
            // Not retryable: the destination is wrong, not unavailable. Retrying would burn the
            // attempt budget on a request that can never be made.
            return Unsendable(eventType, started, "The destination URL is not a permitted target.");
        }

        if (!_secrets.TryUnprotect(secretEncrypted, out byte[] secret))
        {
            // Explicit deny. Sending unsigned, or signed with a different secret, would either be
            // rejected by the receiver or — worse — accepted (SHARED-KERNEL §17.9).
            return Unsendable(
                eventType,
                started,
                "The subscription secret could not be decrypted; the delivery was not sent.");
        }

        try
        {
            return await PostAsync(url, secret, eventType, payloadJson, started, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>Builds, signs and sends the request.</summary>
    private async Task<WebhookAttempt> PostAsync(
        string url,
        byte[] secret,
        string eventType,
        string payloadJson,
        long started,
        CancellationToken cancellationToken)
    {
        WebhookOptions options = _options.CurrentValue;

        // The bytes that are signed and the bytes that are sent are the same array. Nothing
        // re-serializes between here and the socket (TC-165).
        byte[] body = Encoding.UTF8.GetBytes(payloadJson);

        ISigner? signer;

        try
        {
            signer = await _keys.GetSignerAsync(cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            // No usable webhook key. The symmetric slot alone would still verify for the customer,
            // but silently dropping the slot a third party relies on is exactly the kind of quiet
            // degradation §E.5.3 is trying to avoid, so the delivery is refused and counted.
            LogNoSigningKey(_logger, exception);
            return Unsendable(eventType, started, "No webhook signing key is available.");
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();

        using HttpRequestMessage request = new(HttpMethod.Post, url);
        using ByteArrayContent content = new(body);

        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(ContentType)
        {
            CharSet = "utf-8",
        };

        request.Content = content;
        request.Headers.TryAddWithoutValidation(
            WebhookSignature.SignatureHeader,
            WebhookSignature.Create(body, secret, signer, now));
        request.Headers.TryAddWithoutValidation(WebhookSignature.AlgorithmHeader, WebhookSignature.AlgorithmValue);
        request.Headers.TryAddWithoutValidation("DLE-Event", eventType);

        HttpClient client = _clients.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

        try
        {
            using HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            int status = (int)response.StatusCode;
            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);

            if (response.IsSuccessStatusCode)
            {
                _metrics.Attempt(eventType, WebhookAttempt.Delivered, elapsed);
                return new WebhookAttempt(WebhookAttempt.Delivered, status, null, elapsed, Retryable: false);
            }

            _metrics.Attempt(eventType, WebhookAttempt.Failed, elapsed);

            return new WebhookAttempt(
                WebhookAttempt.Failed,
                status,
                Clip(string.Create(CultureInfo.InvariantCulture, $"The endpoint answered {status} {response.ReasonPhrase}.")),
                elapsed,
                IsRetryable(response.StatusCode));
        }
        catch (HttpRequestException exception)
        {
            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
            _metrics.Attempt(eventType, WebhookAttempt.Failed, elapsed);

            // The message can name the destination policy that refused the address (T-02), which is
            // information the operator needs and the customer's endpoint never sees.
            return new WebhookAttempt(
                WebhookAttempt.Failed,
                null,
                Clip(exception.Message),
                elapsed,
                Retryable: true);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
            _metrics.Attempt(eventType, WebhookAttempt.Failed, elapsed);

            return new WebhookAttempt(
                WebhookAttempt.Failed,
                null,
                Clip(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The endpoint did not answer within {options.TimeoutSeconds} seconds.")),
                elapsed,
                Retryable: true);
        }
    }

    /// <summary>Builds the outcome of a delivery that was never put on the wire.</summary>
    private WebhookAttempt Unsendable(string eventType, long started, string error)
    {
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        _metrics.Attempt(eventType, WebhookAttempt.Unsendable, elapsed);

        return new WebhookAttempt(WebhookAttempt.Unsendable, null, error, elapsed, Retryable: false);
    }

    /// <summary>
    /// Whether a status code is worth trying again.
    /// </summary>
    /// <param name="status">The status the endpoint returned.</param>
    /// <returns><see langword="true"/> when a retry could plausibly succeed.</returns>
    /// <remarks>
    /// A 4xx other than 408 and 429 means the receiver understood the request and refused it;
    /// repeating it changes nothing and only spends the customer's own capacity. 3xx counts as a
    /// refusal too: the client does not follow redirects, because a redirect the caller never sees
    /// is a redirect the caller cannot judge (T-02).
    /// </remarks>
    private static bool IsRetryable(HttpStatusCode status) => status switch
    {
        HttpStatusCode.RequestTimeout => true,
        HttpStatusCode.TooManyRequests => true,
        _ => (int)status >= 500,
    };

    /// <summary>Truncates an error so a hostile endpoint cannot grow the delivery row.</summary>
    private static string Clip(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "The delivery failed.";
        }

        string trimmed = value.Trim();

        return trimmed.Length <= MaxErrorLength ? trimmed : trimmed[..MaxErrorLength];
    }

    [LoggerMessage(
        EventId = 5502,
        Level = LogLevel.Error,
        Message = "A webhook delivery could not be signed and was not sent.")]
    private static partial void LogNoSigningKey(ILogger logger, Exception exception);
}
