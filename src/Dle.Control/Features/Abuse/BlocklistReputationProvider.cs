using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using Dle.Domain.Abuse;
using Dle.Domain.Primitives;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dle.Control.Features.Abuse;

/// <summary>
/// Consults the operator's own list of blocked hosts.
/// </summary>
/// <remarks>
/// <para>
/// This is the source that lets a deployment answer a complaint in the minute it arrives instead of
/// waiting for a third party feed to catch up, and it is the source an operator can be held to:
/// it is a file they wrote. It is consulted before any remote source, and its verdict is final.
/// </para>
/// <para>
/// The file is a host list, not a URL list, because that is the unit abuse actually moves in — one
/// compromised host serves a new path per victim. Entries are normalised through
/// <see cref="HostNormalizer"/>, so <c>WWW.Example.COM.</c> and <c>example.com</c> are one entry,
/// and a listed host also blocks its subdomains.
/// </para>
/// </remarks>
public sealed class BlocklistReputationProvider : IUrlReputationProvider
{
    /// <summary>Value of <see cref="Source"/>, matching the shared kernel's list of sources.</summary>
    public const string SourceName = "blocklist";

    private readonly IOptionsMonitor<AbuseOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BlocklistReputationProvider> _logger;
    private readonly Lock _gate = new();

    private HashSet<string> _hosts = new(StringComparer.Ordinal);
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private string? _loadedPath;

    /// <summary>Creates the provider.</summary>
    /// <param name="options">Abuse options, read for the file path and the reload interval.</param>
    /// <param name="timeProvider">Clock used to decide when to re-read the file.</param>
    /// <param name="logger">Logger.</param>
    public BlocklistReputationProvider(
        IOptionsMonitor<AbuseOptions> options,
        TimeProvider timeProvider,
        ILogger<BlocklistReputationProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Source => SourceName;

    /// <inheritdoc />
    public bool IsEnabled => !string.IsNullOrWhiteSpace(_options.CurrentValue.BlocklistPath);

    /// <inheritdoc />
    public ValueTask<UrlSafetyVerdict?> CheckAsync(Uri url, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        ct.ThrowIfCancellationRequested();

        AbuseOptions options = _options.CurrentValue;

        if (string.IsNullOrWhiteSpace(options.BlocklistPath))
        {
            return ValueTask.FromResult<UrlSafetyVerdict?>(null);
        }

        HashSet<string> hosts = GetHosts(options);

        if (hosts.Count == 0 || !HostNormalizer.TryNormalize(url.Host, out string host))
        {
            return ValueTask.FromResult<UrlSafetyVerdict?>(null);
        }

        // A listed host blocks its subdomains too: an abuser who has one host has all of them.
        for (int index = 0; index >= 0; index = host.IndexOf('.', index + 1))
        {
            string candidate = index == 0 ? host : host[(index + 1)..];

            if (hosts.Contains(candidate))
            {
                return ValueTask.FromResult<UrlSafetyVerdict?>(UrlSafetyVerdict.Reject(
                    UrlSafetyLevel.Blocked,
                    SourceName,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The host '{candidate}' is on the operator's blocklist.")) with
                {
                    CheckedAt = _timeProvider.GetUtcNow(),
                });
            }
        }

        // Not being on a local blocklist is not evidence of safety, so this source stays silent
        // rather than approving.
        return ValueTask.FromResult<UrlSafetyVerdict?>(null);
    }

    /// <summary>Returns the current list, re-reading the file when it has gone stale.</summary>
    /// <param name="options">The current options.</param>
    /// <returns>The blocked hosts.</returns>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification =
            "A blocklist file that cannot be read must not take the control plane down, and must "
            + "not silently become an empty list either: the previous contents stay in force and "
            + "the failure is logged as an error (SHARED-KERNEL §17.9).")]
    private HashSet<string> GetHosts(AbuseOptions options)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        lock (_gate)
        {
            bool pathChanged = !string.Equals(_loadedPath, options.BlocklistPath, StringComparison.Ordinal);
            bool stale = now - _loadedAt >= TimeSpan.FromMinutes(options.BlocklistReloadMinutes);

            if (!pathChanged && !stale)
            {
                return _hosts;
            }

            _loadedAt = now;
            _loadedPath = options.BlocklistPath;

            try
            {
                HashSet<string> loaded = new(StringComparer.Ordinal);

                if (File.Exists(options.BlocklistPath))
                {
                    foreach (string line in File.ReadLines(options.BlocklistPath!))
                    {
                        string entry = line.Trim();

                        if (entry.Length == 0 || entry.StartsWith('#'))
                        {
                            continue;
                        }

                        if (HostNormalizer.TryNormalize(entry, out string normalized))
                        {
                            loaded.Add(normalized);
                        }
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "The configured abuse blocklist file does not exist; no local entries are in force.");
                }

                _hosts = loaded;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "The abuse blocklist could not be read. The previously loaded {EntryCount} entries stay in force.",
                    _hosts.Count);
            }

            return _hosts;
        }
    }
}
