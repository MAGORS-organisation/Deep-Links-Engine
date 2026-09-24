using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

using Dle.Domain.Abuse;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dle.Control.Features.Abuse;

/// <summary>
/// Consults URLhaus (abuse.ch) about a target URL (§E.3 step 3).
/// </summary>
/// <remarks>
/// <para>
/// URLhaus is the default reputation source because it is the only one of the well known feeds that
/// an open-source project can point a commercial self-hoster at without a licence conversation:
/// Safe Browsing v5 is restricted to non-commercial use and Web Risk is paid. It is narrower than
/// either — it tracks malware distribution, not phishing in general — which is exactly why it is a
/// provider behind a seam rather than the answer.
/// </para>
/// <para>
/// The response is read with <see cref="JsonDocument"/> rather than deserialised into a type. The
/// document is a third party's contract that changes without notice, and reading the three members
/// that matter is more robust than binding a shape; it also keeps reflective serialization out of
/// the assembly entirely (SHARED-KERNEL §0).
/// </para>
/// </remarks>
public sealed class UrlHausReputationProvider : IUrlReputationProvider
{
    /// <summary>Name of the HTTP client this provider resolves.</summary>
    public const string HttpClientName = "dle-urlhaus";

    /// <summary>Value of <see cref="Source"/>, matching the shared kernel's list of sources.</summary>
    public const string SourceName = "urlhaus";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<AbuseOptions> _options;
    private readonly AbuseMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<UrlHausReputationProvider> _logger;

    /// <summary>Creates the provider.</summary>
    /// <param name="httpClientFactory">Factory for the named client.</param>
    /// <param name="options">Abuse options.</param>
    /// <param name="metrics">Instruments.</param>
    /// <param name="timeProvider">Clock used to stamp the verdict.</param>
    /// <param name="logger">Logger.</param>
    public UrlHausReputationProvider(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AbuseOptions> options,
        AbuseMetrics metrics,
        TimeProvider timeProvider,
        ILogger<UrlHausReputationProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _options = options;
        _metrics = metrics;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Source => SourceName;

    /// <inheritdoc />
    public bool IsEnabled => _options.CurrentValue.UrlHausEnabled;

    /// <inheritdoc />
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification =
            "A reputation source is an optional opinion from a third party. Whatever its client "
            + "throws — transport, timeout, malformed JSON — the answer is 'no opinion', which the "
            + "caller already treats as not-approved. The failure is logged and counted on "
            + "dle_abuse_lookup_failures rather than swallowed (SHARED-KERNEL §17.9).")]
    public async ValueTask<UrlSafetyVerdict?> CheckAsync(Uri url, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);

        AbuseOptions options = _options.CurrentValue;

        if (!options.UrlHausEnabled)
        {
            return null;
        }

        try
        {
            using HttpClient client = _httpClientFactory.CreateClient(HttpClientName);
            client.Timeout = TimeSpan.FromSeconds(options.LookupTimeoutSeconds);

            using FormUrlEncodedContent content = new(
                [new KeyValuePair<string, string>("url", url.AbsoluteUri)]);

            using HttpRequestMessage request = new(HttpMethod.Post, options.UrlHausEndpoint)
            {
                Content = content,
            };

            if (!string.IsNullOrWhiteSpace(options.UrlHausAuthKey))
            {
                request.Headers.TryAddWithoutValidation("Auth-Key", options.UrlHausAuthKey);
            }

            using HttpResponseMessage response = await client.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                _metrics.LookupFailed(SourceName);
                _logger.LogWarning(
                    "URLhaus answered {StatusCode}; the target was judged without its opinion.",
                    (int)response.StatusCode);
                return null;
            }

            await using Stream body = await response.Content.ReadAsStreamAsync(ct);
            using JsonDocument document = await JsonDocument.ParseAsync(body, default, ct);

            return Interpret(document.RootElement);
        }
        catch (Exception ex)
        {
            _metrics.LookupFailed(SourceName);
            _logger.LogWarning(
                ex,
                "A URLhaus lookup failed; the target was judged without its opinion.");
            return null;
        }
    }

    /// <summary>Turns a URLhaus response document into a verdict.</summary>
    /// <param name="root">The root element of the response.</param>
    /// <returns>The verdict, or <see langword="null"/> when the source has no opinion.</returns>
    private UrlSafetyVerdict? Interpret(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("query_status", out JsonElement statusElement)
            || statusElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? queryStatus = statusElement.GetString();

        // "no_results" is a genuine clean answer from this source: URLhaus knows the URL space it
        // tracks, and not being in it is information. Anything else — "invalid_url", an auth
        // failure, a status this code does not know — is not an opinion.
        if (string.Equals(queryStatus, "no_results", StringComparison.Ordinal))
        {
            return Stamp(UrlSafetyVerdict.Safe(SourceName));
        }

        if (!string.Equals(queryStatus, "ok", StringComparison.Ordinal))
        {
            return null;
        }

        string threat = ReadString(root, "threat") ?? "unknown";
        string urlStatus = ReadString(root, "url_status") ?? "unknown";

        // A listing that is "offline" is still a listing: the target was serving malware recently
        // enough to be catalogued, and a short link pointing at it is not something to wave through.
        // It is reported as suspicious rather than malicious so that the operator's own policy
        // decides whether that blocks creation.
        UrlSafetyLevel level = string.Equals(urlStatus, "online", StringComparison.Ordinal)
            ? UrlSafetyLevel.Malicious
            : UrlSafetyLevel.Suspicious;

        return Stamp(UrlSafetyVerdict.Reject(
            level,
            SourceName,
            string.Create(
                CultureInfo.InvariantCulture,
                $"URLhaus lists this URL as {threat} and currently {urlStatus}.")));
    }

    /// <summary>Reads an optional string member.</summary>
    /// <param name="root">The object to read from.</param>
    /// <param name="name">Member name.</param>
    /// <returns>The value, or <see langword="null"/>.</returns>
    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    /// <summary>Records when the verdict was reached, so a cached one can be aged out.</summary>
    /// <param name="verdict">The verdict.</param>
    /// <returns>The verdict with its instant filled in.</returns>
    private UrlSafetyVerdict Stamp(UrlSafetyVerdict verdict) =>
        verdict with { CheckedAt = _timeProvider.GetUtcNow() };
}
