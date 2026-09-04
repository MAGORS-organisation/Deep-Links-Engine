using System.Net;
using System.Net.Sockets;
using System.Text.Json;

using Dle.Control.Configuration;

using Microsoft.Extensions.Options;

namespace Dle.Control.Features.Domains;

/// <summary>
/// Fetches and judges a domain's association files (FR-143, FR-144).
/// </summary>
/// <remarks>
/// <para>
/// Universal Links and App Links fail silently. There is no error on the device, no log line and no
/// crash — the link simply opens in the browser, and the customer reports "deep links stopped
/// working" weeks later. Everything this class checks is one of the ways that happens: a redirect on
/// the association file, the wrong content type, a bundle identifier that does not match, an Android
/// fingerprint taken from the upload keystore instead of Play App Signing.
/// </para>
/// <para>
/// The fetch goes through the SSRF-guarded client. A verification target is a customer-supplied host
/// name, which makes this an outbound request to an address the customer chooses — exactly the shape
/// of T-02 — so the connection is validated at the socket, not at the name, and redirects are never
/// followed.
/// </para>
/// </remarks>
public sealed class DomainVerificationService
{
    /// <summary>Name of the guarded HTTP client used to fetch association files.</summary>
    public const string HttpClientName = "dle-well-known";

    /// <summary>Path of the Apple App Site Association file (§A.2.1).</summary>
    public const string AasaPath = "/.well-known/apple-app-site-association";

    /// <summary>Path of the Digital Asset Links file (§A.2.2).</summary>
    public const string AssetLinksPath = "/.well-known/assetlinks.json";

    /// <summary>Stored verification kind for the name resolution check.</summary>
    public const string DnsKind = "dns";

    /// <summary>Stored verification kind for the certificate check.</summary>
    public const string TlsKind = "tls";

    /// <summary>Stored verification kind for the Apple association file.</summary>
    public const string AasaKind = "aasa";

    /// <summary>Stored verification kind for the Android association file.</summary>
    public const string AssetLinksKind = "assetlinks";

    /// <summary>Outcome recorded when a check passed.</summary>
    public const string OkStatus = "ok";

    /// <summary>Outcome recorded when a check passed but something will bite later.</summary>
    public const string WarningStatus = "warning";

    /// <summary>Outcome recorded when a check failed.</summary>
    public const string FailedStatus = "failed";

    /// <summary>Largest association file read, so a hostile host cannot exhaust memory.</summary>
    private const int MaxBodyBytes = 256 * 1024;

    private readonly IHttpClientFactory _clients;
    private readonly AppRepository _apps;
    private readonly TimeProvider _timeProvider;
    private readonly DleControlOptions _options;
    private readonly ILogger<DomainVerificationService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="clients">Factory of the SSRF-guarded HTTP client.</param>
    /// <param name="apps">Application storage, for the identifiers the files must contain.</param>
    /// <param name="timeProvider">Clock used to stamp the run.</param>
    /// <param name="options">Control-plane options, for the propagation notice.</param>
    /// <param name="logger">Logger.</param>
    public DomainVerificationService(
        IHttpClientFactory clients,
        AppRepository apps,
        TimeProvider timeProvider,
        IOptions<DleControlOptions> options,
        ILogger<DomainVerificationService> logger)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _clients = clients;
        _apps = apps;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Runs every check against one domain.</summary>
    /// <param name="domain">The domain to verify.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result, including the runs to record.</returns>
    public async Task<DomainVerificationRun> VerifyAsync(
        LinkDomain domain,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domain);

        DateTimeOffset checkedAt = _timeProvider.GetUtcNow();
        List<VerificationCheckResult> checks = [];
        List<DomainVerification> runs = [];

        VerificationCheckResult dns = await CheckDnsAsync(domain.Host, cancellationToken);
        checks.Add(dns);
        runs.Add(Record(domain.Id, DnsKind, dns, checkedAt, httpStatus: null, redirects: 0));

        IReadOnlyList<AasaAppEntry> iosApps =
            await _apps.GetAasaEntriesAsync(domain.Host, cancellationToken);

        IReadOnlyList<AndroidAppEntry> androidApps =
            await _apps.GetAssetLinkEntriesAsync(domain.Host, cancellationToken);

        FetchResult aasa = await FetchAsync(domain.Host, AasaPath, cancellationToken);

        // The certificate check is the fetch: an HTTPS request that completed is a request whose
        // certificate chain validated, and one that failed at the transport is exactly what a
        // customer means by "the certificate is wrong".
        VerificationCheckResult tls = aasa.TransportFailed
            ? new VerificationCheckResult
            {
                Kind = TlsKind,
                Status = FailedStatus,
                Codes = ["tls.unreachable"],
                Detail = aasa.TransportError,
            }
            : new VerificationCheckResult
            {
                Kind = TlsKind,
                Status = OkStatus,
                Detail = "The host answered over HTTPS with a certificate this engine trusts.",
            };

        checks.Add(tls);
        runs.Add(Record(domain.Id, TlsKind, tls, checkedAt, aasa.StatusCode, aasa.RedirectCount));

        VerificationCheckResult aasaCheck = JudgeAasa(aasa, iosApps);
        checks.Add(aasaCheck);
        runs.Add(Record(domain.Id, AasaKind, aasaCheck, checkedAt, aasa.StatusCode, aasa.RedirectCount));

        FetchResult assetLinks = await FetchAsync(domain.Host, AssetLinksPath, cancellationToken);
        VerificationCheckResult assetCheck = JudgeAssetLinks(assetLinks, androidApps);
        checks.Add(assetCheck);
        runs.Add(Record(
            domain.Id,
            AssetLinksKind,
            assetCheck,
            checkedAt,
            assetLinks.StatusCode,
            assetLinks.RedirectCount));

        bool ok = true;

        foreach (VerificationCheckResult check in checks)
        {
            if (string.Equals(check.Status, FailedStatus, StringComparison.Ordinal))
            {
                ok = false;
                break;
            }
        }

        DomainVerificationResponse response = new()
        {
            DomainId = domain.Id,
            Host = domain.Host,
            Ok = ok,
            CheckedAt = checkedAt,
            Checks = checks,
            PropagationNotice = PropagationNotice(),
        };

        return new DomainVerificationRun(response, runs);
    }

    /// <summary>
    /// The seven-day propagation notice shown after every verification (TC-125, §A.2.1, §A.2.2).
    /// </summary>
    /// <returns>The notice, in English and Slovak.</returns>
    /// <remarks>
    /// It is returned on success as well as on failure, and that is deliberate. The failure mode this
    /// prevents is an operator who fixes an association file, sees a green tick, tests on a device
    /// that still holds the old file, concludes the fix did not work and reverts it. Apple's CDN and
    /// Android's verifier both cache for up to a week; nothing the control plane does shortens that,
    /// so the only honest thing to do is say so every time (NFR-15).
    /// </remarks>
    private string PropagationNotice()
    {
        int days = _options.AssociationPropagationDays;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Association file changes reach devices only as their cached copy expires, which takes "
            + $"up to {days} days on both platforms. A device that already visited this host may keep "
            + $"the old file until then. — Zmeny asociačných súborov sa na zariadenia dostanú až s "
            + $"vypršaním ich kópie v cache, čo trvá až {days} dní na oboch platformách.");
    }

    /// <summary>Resolves the host.</summary>
    /// <param name="host">The host.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The check result.</returns>
    private async Task<VerificationCheckResult> CheckDnsAsync(
        string host,
        CancellationToken cancellationToken)
    {
        if (TargetUrlPolicy.IsForbiddenHost(host))
        {
            return new VerificationCheckResult
            {
                Kind = DnsKind,
                Status = FailedStatus,
                Codes = ["dns.internal_name"],
                Detail = "The host is an internal name and can never serve public links.",
            };
        }

        try
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);

            if (addresses.Length == 0)
            {
                return new VerificationCheckResult
                {
                    Kind = DnsKind,
                    Status = FailedStatus,
                    Codes = ["dns.unresolved"],
                    Detail = "The host does not resolve to any address.",
                };
            }

            foreach (IPAddress address in addresses)
            {
                if (TargetUrlPolicy.IsForbiddenAddress(address))
                {
                    return new VerificationCheckResult
                    {
                        Kind = DnsKind,
                        Status = FailedStatus,
                        Codes = ["dns.private_address"],
                        Detail = "The host resolves to an address that is not routable on the public "
                            + "internet, so devices could not reach it.",
                    };
                }
            }

            return new VerificationCheckResult
            {
                Kind = DnsKind,
                Status = OkStatus,
                Detail = "The host resolves to a public address.",
            };
        }
        catch (SocketException exception)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Name resolution failed for {Host} during verification: {SocketError}.",
                    host,
                    exception.SocketErrorCode);
            }

            return new VerificationCheckResult
            {
                Kind = DnsKind,
                Status = FailedStatus,
                Codes = ["dns.unresolved"],
                Detail = "The host could not be resolved.",
            };
        }
    }

    /// <summary>Fetches one association file.</summary>
    /// <param name="host">The host to fetch from.</param>
    /// <param name="path">The well-known path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the host answered.</returns>
    /// <remarks>
    /// Redirects are counted, not followed. Both Apple and Google refuse a redirected association
    /// file, so following one would report a success the devices will never see — this is one of the
    /// most common silent breakages there is (TC-124).
    /// </remarks>
    private async Task<FetchResult> FetchAsync(
        string host,
        string path,
        CancellationToken cancellationToken)
    {
        Uri uri = new(string.Create(CultureInfo.InvariantCulture, $"https://{host}{path}"));
        HttpClient client = _clients.CreateClient(HttpClientName);

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, uri);
            using HttpResponseMessage response =
                await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            int status = (int)response.StatusCode;
            int redirects = status is >= 300 and <= 399 ? 1 : 0;

            string? body = null;

            if (response.Content.Headers.ContentLength is null or <= MaxBodyBytes)
            {
                using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using StreamReader reader = new(stream);

                char[] buffer = new char[MaxBodyBytes];
                int read = await reader.ReadBlockAsync(buffer, cancellationToken);
                body = new string(buffer, 0, read);
            }

            return new FetchResult(
                status,
                redirects,
                response.Content.Headers.ContentType?.MediaType,
                body,
                TransportFailed: false,
                TransportError: null);
        }
        catch (HttpRequestException exception)
        {
            // Fail closed and say what happened. A verification that cannot reach the host is a
            // failed verification, never an assumed pass (SHARED-KERNEL §17.9).
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Association file fetch failed for {Host}{Path}: {Reason}.",
                    host,
                    path,
                    exception.Message);
            }

            return new FetchResult(0, 0, null, null, TransportFailed: true, exception.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Association file fetch timed out for {Host}{Path}.",
                    host,
                    path);
            }

            return new FetchResult(
                0,
                0,
                null,
                null,
                TransportFailed: true,
                "The host did not answer in time.");
        }
    }

    /// <summary>Judges the Apple association file.</summary>
    /// <param name="fetch">What the host answered.</param>
    /// <param name="apps">The iOS applications registered for this host.</param>
    /// <returns>The check result.</returns>
    private static VerificationCheckResult JudgeAasa(
        FetchResult fetch,
        IReadOnlyList<AasaAppEntry> apps)
    {
        if (apps.Count == 0)
        {
            return new VerificationCheckResult
            {
                Kind = AasaKind,
                Status = OkStatus,
                Detail = "No iOS application is registered for this host, so no association file is "
                    + "expected.",
            };
        }

        List<string> expected = new(apps.Count);

        foreach (AasaAppEntry app in apps)
        {
            expected.Add(app.AppId);
        }

        if (fetch.TransportFailed)
        {
            return new VerificationCheckResult
            {
                Kind = AasaKind,
                Status = FailedStatus,
                Codes = [WellKnownValidator.ErrStatus],
                Detail = fetch.TransportError,
            };
        }

        return Judge(
            AasaKind,
            WellKnownValidator.ValidateAasa(
                fetch.Body,
                fetch.ContentType,
                fetch.StatusCode,
                fetch.RedirectCount,
                expected));
    }

    /// <summary>Judges the Android association file.</summary>
    /// <param name="fetch">What the host answered.</param>
    /// <param name="apps">The Android applications registered for this host.</param>
    /// <returns>The check result.</returns>
    private static VerificationCheckResult JudgeAssetLinks(
        FetchResult fetch,
        IReadOnlyList<AndroidAppEntry> apps)
    {
        if (apps.Count == 0)
        {
            return new VerificationCheckResult
            {
                Kind = AssetLinksKind,
                Status = OkStatus,
                Detail = "No Android application is registered for this host, so no association file "
                    + "is expected.",
            };
        }

        List<string> expected = [];

        foreach (AndroidAppEntry app in apps)
        {
            expected.AddRange(app.Sha256CertFingerprints);
        }

        if (fetch.TransportFailed)
        {
            return new VerificationCheckResult
            {
                Kind = AssetLinksKind,
                Status = FailedStatus,
                Codes = [WellKnownValidator.ErrStatus],
                Detail = fetch.TransportError,
            };
        }

        return Judge(
            AssetLinksKind,
            WellKnownValidator.ValidateAssetLinks(
                fetch.Body,
                fetch.ContentType,
                fetch.StatusCode,
                fetch.RedirectCount,
                expected));
    }

    /// <summary>Turns validator issues into one check result.</summary>
    /// <param name="kind">Which check this is.</param>
    /// <param name="issues">The issues the validator reported.</param>
    /// <returns>The check result.</returns>
    /// <remarks>
    /// A warning is kept distinct from a failure rather than folded into it. The Play App Signing
    /// warning is the reason that distinction exists: the file is valid, the verification passes, and
    /// the application will still fail to open links in production (FR-144, TC-123).
    /// </remarks>
    private static VerificationCheckResult Judge(
        string kind,
        IReadOnlyList<WellKnownValidationIssue> issues)
    {
        if (issues.Count == 0)
        {
            return new VerificationCheckResult
            {
                Kind = kind,
                Status = OkStatus,
                Detail = "The association file is present, well formed and lists the expected "
                    + "identifiers.",
            };
        }

        List<string> codes = new(issues.Count);
        List<string> messages = new(issues.Count);
        bool failed = false;

        foreach (WellKnownValidationIssue issue in issues)
        {
            codes.Add(issue.Code);
            messages.Add(issue.Message);
            failed |= issue.IsError;
        }

        return new VerificationCheckResult
        {
            Kind = kind,
            Status = failed ? FailedStatus : WarningStatus,
            Codes = codes,
            Detail = string.Join(" ", messages),
        };
    }

    /// <summary>Builds the row that records one check.</summary>
    /// <param name="domainId">The domain.</param>
    /// <param name="kind">Which check this is.</param>
    /// <param name="check">The result.</param>
    /// <param name="checkedAt">When the run happened.</param>
    /// <param name="httpStatus">What the host answered, when a request was made.</param>
    /// <param name="redirects">How many redirects were seen.</param>
    /// <returns>The row.</returns>
    private static DomainVerification Record(
        Guid domainId,
        string kind,
        VerificationCheckResult check,
        DateTimeOffset checkedAt,
        int? httpStatus,
        int redirects) => new()
        {
            DomainId = domainId,
            Kind = kind,
            Status = check.Status,
            HttpStatus = httpStatus == 0 ? null : httpStatus,
            RedirectCount = redirects,
            Issues = JsonSerializer.Serialize(check.Codes, DleJson.Default),
            CheckedAt = checkedAt,
        };

    /// <summary>What a host answered for one association file.</summary>
    /// <param name="StatusCode">HTTP status, or zero when the request never completed.</param>
    /// <param name="RedirectCount">How many redirects were seen; any is a failure.</param>
    /// <param name="ContentType">Media type the host declared.</param>
    /// <param name="Body">The body, truncated at the maximum this engine reads.</param>
    /// <param name="TransportFailed">Whether the request failed below HTTP.</param>
    /// <param name="TransportError">What went wrong at the transport.</param>
    private sealed record FetchResult(
        int StatusCode,
        int RedirectCount,
        string? ContentType,
        string? Body,
        bool TransportFailed,
        string? TransportError);
}

/// <summary>
/// The outcome of one verification run: what to answer, and what to record (FR-143).
/// </summary>
/// <param name="Response">The answer to the caller.</param>
/// <param name="Runs">The rows to append to the verification history.</param>
public sealed record DomainVerificationRun(
    DomainVerificationResponse Response,
    IReadOnlyList<DomainVerification> Runs);
