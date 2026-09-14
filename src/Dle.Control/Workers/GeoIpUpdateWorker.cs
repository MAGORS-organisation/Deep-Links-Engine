using System.Net.Http;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dle.Control.Workers;

/// <summary>
/// Refreshes the GeoIP database out of band (§C.4 <c>Dle:Edge:GeoIp</c>, NFR-14).
/// </summary>
/// <remarks>
/// <para>
/// §E.6.3 forbids an outgoing call to a third party from the hot path, which is why the edge
/// resolves geography against a local database file and never against a service. A local file has
/// to be refreshed by something, and that something must not be the edge: a resolver that downloads
/// a two hundred megabyte archive while serving redirects is a latency incident waiting for a
/// Tuesday. So the refresh happens here, in the control plane, on a leader-elected schedule, and
/// the edge simply notices that the file on disk changed.
/// </para>
/// <para>
/// Disabled by default, and that is a licensing decision rather than caution. GeoLite2 needs a
/// MaxMind account and a licence key, and the licence is the deployment's, not the engine's. A
/// deployment that ships the file by other means — a container layer, a configuration management
/// system, an air-gapped copy — leaves this off, which is why the edge treats a missing database as
/// "no geography" rather than as an error.
/// </para>
/// <para>
/// The write is atomic: the archive is downloaded to a temporary file beside the destination and
/// then moved over it. A half-written database is worse than an old one, because the reader opens
/// it successfully and answers wrongly.
/// </para>
/// </remarks>
public sealed partial class GeoIpUpdateWorker : LeaderElectedBackgroundService
{
    /// <summary>Job name, used as the advisory lock key and the metric tag.</summary>
    public const string Job = "dle.worker.geoip-update";

    /// <summary>Name of the outbound client used for the download.</summary>
    public const string HttpClientName = "dle-geoip";

    private readonly IHttpClientFactory _clients;
    private readonly IOptionsMonitor<WorkerOptions> _options;
    private readonly ILogger<GeoIpUpdateWorker> _logger;

    /// <summary>
    /// Creates the worker.
    /// </summary>
    /// <param name="clients">Factory of the outbound client.</param>
    /// <param name="leader">Leader election.</param>
    /// <param name="metrics">Instruments.</param>
    /// <param name="options">Worker options.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public GeoIpUpdateWorker(
        IHttpClientFactory clients,
        PostgresLeaderLock leader,
        WorkerMetrics metrics,
        IOptionsMonitor<WorkerOptions> options,
        TimeProvider timeProvider,
        ILogger<GeoIpUpdateWorker> logger)
        : base(leader, metrics, timeProvider, logger)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _clients = clients;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override string JobName => Job;

    /// <inheritdoc />
    protected override bool IsEnabled
    {
        get
        {
            GeoIpUpdateWorkerOptions geoIp = _options.CurrentValue.GeoIpUpdate;

            return _options.CurrentValue.Enabled
                && geoIp.Enabled
                && !string.IsNullOrWhiteSpace(geoIp.DownloadUrl)
                && !string.IsNullOrWhiteSpace(geoIp.DatabasePath);
        }
    }

    /// <inheritdoc />
    protected override TimeSpan Interval =>
        TimeSpan.FromMinutes(Math.Max(_options.CurrentValue.GeoIpUpdate.IntervalMinutes, 1));

    /// <inheritdoc />
    protected override bool RunAtStartup => _options.CurrentValue.GeoIpUpdate.RunAtStartup;

    /// <inheritdoc />
    protected override TimeSpan LockTimeout => TimeSpan.FromSeconds(_options.CurrentValue.LockTimeoutSeconds);

    /// <inheritdoc />
    protected override TimeSpan StartupJitter => TimeSpan.FromSeconds(_options.CurrentValue.StartupJitterSeconds);

    /// <inheritdoc />
    protected override async Task<string> RunPassAsync(CancellationToken cancellationToken)
    {
        GeoIpUpdateWorkerOptions options = _options.CurrentValue.GeoIpUpdate;

        if (string.IsNullOrWhiteSpace(options.DownloadUrl) || string.IsNullOrWhiteSpace(options.DatabasePath))
        {
            // Cannot happen while IsEnabled holds, but the guard keeps this method honest if it is
            // ever called directly by a test (SHARED-KERNEL §17.9).
            return "no download URL or database path is configured; nothing to do.";
        }

        Uri source = BuildUrl(options.DownloadUrl, options.LicenseKey);

        string destination = Path.GetFullPath(options.DatabasePath);
        string? directory = Path.GetDirectoryName(destination);

        if (!string.IsNullOrEmpty(directory))
        {
            _ = Directory.CreateDirectory(directory);
        }

        string temporary = destination + ".download";

        HttpClient client = _clients.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

        long written;

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, source);
            using HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Reported as a failed pass, not swallowed. A licence key that has been revoked
                // fails exactly like this, and it fails every night until somebody is told.
                throw new InvalidOperationException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The GeoIP source answered {(int)response.StatusCode}."));
            }

            if (response.Content.Headers.ContentLength is { } declared && declared > options.MaxDownloadBytes)
            {
                throw new InvalidOperationException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The GeoIP archive is {declared} bytes, above the configured maximum of {options.MaxDownloadBytes}."));
            }

            written = await DownloadAsync(response, temporary, options.MaxDownloadBytes, cancellationToken);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }

        // Atomic swap. A reader either sees the previous database or the new one, never a partial
        // file it would happily open and answer from.
        File.Move(temporary, destination, overwrite: true);

        Metrics.Items(Job, "geoip_bytes", written);
        LogUpdated(_logger, destination, written);

        return string.Create(CultureInfo.InvariantCulture, $"{written} bytes written to {destination}.");
    }

    /// <summary>Streams the response to a temporary file, refusing to exceed the bound.</summary>
    private static async Task<long> DownloadAsync(
        HttpResponseMessage response,
        string temporary,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using FileStream destination = new(
            temporary,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        byte[] buffer = new byte[81920];
        long total = 0;

        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);

            if (read == 0)
            {
                break;
            }

            total += read;

            if (total > maxBytes)
            {
                // A server that ignores the declared length, or a redirect to something else
                // entirely, must not be able to fill the volume the database lives on.
                throw new InvalidOperationException(
                    "The GeoIP archive exceeded the configured maximum size while downloading.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        await destination.FlushAsync(cancellationToken);

        return total;
    }

    /// <summary>Appends the licence key to the configured URL when one is set.</summary>
    /// <remarks>
    /// MaxMind's permalink form takes the key as a query parameter. It is a credential in a URL,
    /// which is unpleasant and is the provider's design; it is kept out of every log line here by
    /// only ever logging the destination path, never the source.
    /// </remarks>
    private static Uri BuildUrl(string downloadUrl, string? licenseKey)
    {
        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            return new Uri(downloadUrl);
        }

        UriBuilder builder = new(downloadUrl);
        string suffix = "license_key=" + Uri.EscapeDataString(licenseKey);

        builder.Query = string.IsNullOrEmpty(builder.Query)
            ? suffix
            : builder.Query.TrimStart('?') + "&" + suffix;

        return builder.Uri;
    }

    /// <summary>Removes a partial download, ignoring the file already being gone.</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Explicit default: a temporary file that cannot be removed is left behind rather than
            // masking the original failure, which is the one worth reporting.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: the download failure is the interesting error, not the cleanup.
        }
    }

    [LoggerMessage(
        EventId = 5660,
        Level = LogLevel.Information,
        Message = "The GeoIP database at {Path} was refreshed ({Bytes} bytes).")]
    private static partial void LogUpdated(ILogger logger, string path, long bytes);
}
