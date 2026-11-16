using System.Formats.Tar;
using System.IO.Compression;
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
/// The write is atomic: the download goes to a temporary file beside the destination, a
/// <c>.tar.gz</c> is unpacked, the result is checked to be a MaxMind database, and only then is it
/// moved over the live file. A half-written or foreign file is worse than an old database.
/// </para>
/// </remarks>
public sealed partial class GeoIpUpdateWorker : LeaderElectedBackgroundService
{
    /// <summary>Job name, used as the advisory lock key and the metric tag.</summary>
    public const string Job = "dle.worker.geoip-update";

    /// <summary>Name of the outbound client used for the download.</summary>
    public const string HttpClientName = "dle-geoip";

    /// <summary>Suffix of the file an archive is unpacked into, beside the download.</summary>
    private const string UnpackedSuffix = ".mmdb";

    /// <summary>How far from the end of a database its metadata marker may sit.</summary>
    private const int MetadataSearchBytes = 128 * 1024;

    /// <summary>First bytes of a gzip stream.</summary>
    private static readonly byte[] GzipMagic = [0x1F, 0x8B];

    /// <summary>The bytes <c>\xAB\xCD\xEFMaxMind.com</c> that introduce a database's metadata.</summary>
    private static readonly byte[] MetadataMarker =
    [
        0xAB, 0xCD, 0xEF,
        (byte)'M', (byte)'a', (byte)'x', (byte)'M', (byte)'i', (byte)'n', (byte)'d', (byte)'.', (byte)'c', (byte)'o', (byte)'m',
    ];

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
        string database;

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
            database = await UnpackAsync(temporary, options.MaxDownloadBytes, cancellationToken);
        }
        catch
        {
            TryDelete(temporary);
            TryDelete(temporary + UnpackedSuffix);
            throw;
        }

        // Atomic swap. A reader either sees the previous database or the new one, never a partial
        // file it would happily open and answer from.
        File.Move(database, destination, overwrite: true);

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

    /// <summary>
    /// Turns the download into a database file that is safe to put where the edge reads it.
    /// </summary>
    /// <remarks>
    /// MaxMind's permalink serves a <c>.tar.gz</c> with the <c>.mmdb</c> inside a dated directory,
    /// and a server in trouble answers 200 with an HTML page. Either one moved over the live file
    /// would replace a working database with something the reader cannot open, so an archive is
    /// unpacked and whatever is left has to carry the MaxMind metadata marker before it is used.
    /// </remarks>
    /// <returns>The path of the validated database file.</returns>
    internal static async Task<string> UnpackAsync(string downloaded, long maxBytes, CancellationToken cancellationToken)
    {
        string candidate = downloaded;

        if (await StartsWithAsync(downloaded, GzipMagic, cancellationToken))
        {
            candidate = downloaded + UnpackedSuffix;

            try
            {
                await ExtractDatabaseAsync(downloaded, candidate, maxBytes, cancellationToken);
            }
            finally
            {
                TryDelete(downloaded);
            }
        }

        if (!await HasMetadataMarkerAsync(candidate, cancellationToken))
        {
            TryDelete(candidate);

            throw new InvalidOperationException(
                "The GeoIP download is not a MaxMind database; the existing database was left in place.");
        }

        return candidate;
    }

    /// <summary>Copies the first <c>.mmdb</c> entry of a gzipped tar archive to a file.</summary>
    /// <remarks>
    /// The entry name is only ever matched, never used as a path, so an archive cannot choose where
    /// it is written.
    /// </remarks>
    private static async Task ExtractDatabaseAsync(
        string archivePath,
        string destinationPath,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        await using FileStream archive = new(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        await using GZipStream gzip = new(archive, CompressionMode.Decompress);
        await using TarReader tar = new(gzip);

        while (await tar.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
        {
            if (entry.DataStream is null
                || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                || !entry.Name.EndsWith(".mmdb", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entry.Length > maxBytes)
            {
                throw new InvalidOperationException(
                    "The GeoIP database inside the archive exceeds the configured maximum size.");
            }

            await using FileStream output = new(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);

            await entry.DataStream.CopyToAsync(output, cancellationToken);

            return;
        }

        throw new InvalidOperationException("The GeoIP archive contains no .mmdb file.");
    }

    /// <summary>Whether a file begins with the given bytes.</summary>
    private static async Task<bool> StartsWithAsync(string path, byte[] prefix, CancellationToken cancellationToken)
    {
        await using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, useAsync: true);

        byte[] head = new byte[prefix.Length];
        int read = await file.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken);

        return read == prefix.Length && head.AsSpan().SequenceEqual(prefix);
    }

    /// <summary>
    /// Whether a file carries the marker every MaxMind database ends its data section with.
    /// </summary>
    /// <remarks>
    /// The format places the metadata, introduced by this marker, within the last 128 KiB of the
    /// file; it is what the reader itself looks for when it opens a database.
    /// </remarks>
    private static async Task<bool> HasMetadataMarkerAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);

        int tailLength = (int)Math.Min(file.Length, MetadataSearchBytes);
        byte[] tail = new byte[tailLength];

        file.Seek(-tailLength, SeekOrigin.End);
        int read = await file.ReadAtLeastAsync(tail, tailLength, throwOnEndOfStream: false, cancellationToken);

        return tail.AsSpan(0, read).LastIndexOf(MetadataMarker) >= 0;
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
