using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

using Dle.Edge.Configuration;
using Dle.Edge.Telemetry;

using MaxMind.Db;
using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Responses;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dle.Edge.Clients;

/// <summary>
/// Offline geographic lookup over a memory-mapped MaxMind GeoLite2 or GeoIP2 database
/// (FR-122, §B.6.1, NFR-14).
/// </summary>
/// <remarks>
/// <para>
/// NFR-14 is the constraint that shapes this class: the resolve path makes no outbound third-party
/// call, geographic lookup included. The database is therefore a local file, opened once at start-up
/// in <see cref="FileAccessMode.MemoryMapped"/> mode so that the operating system pages the parts
/// actually consulted into memory and shares them between worker processes, and a lookup is a tree
/// walk over that mapping with no allocation beyond the response object.
/// </para>
/// <para>
/// A missing, unreadable or corrupt database is not an error condition (§D.6). Country stays
/// <see langword="null"/>, every geo-dependent routing rule therefore fails to match and evaluation
/// falls through to the default rule, and <c>dle_geoip_available</c> drops to zero so that the
/// operator is told. Nothing on the resolve path ever sees an exception from here — a redirect that
/// fails because a data file was replaced badly would be a self-inflicted outage.
/// </para>
/// <para>
/// Refreshing the file itself is a deployment concern — a sidecar, a cron job, a mounted volume —
/// because downloading it would be the outbound call NFR-14 rules out. What this class does is notice
/// that the file on disk was replaced and swap the reader, which
/// <see cref="GeoIpDatabaseRefresher"/> asks it to do on a timer.
/// </para>
/// </remarks>
public sealed partial class MaxMindGeoIpResolver : IGeoIpResolver, IDisposable
{
    /// <summary>Value of <c>Dle:Edge:GeoIp:Provider</c> that selects this resolver.</summary>
    public const string ProviderName = "MaxMindMmap";

    private readonly GeoIpOptions _options;
    private readonly EdgeMetrics _metrics;
    private readonly ILogger<MaxMindGeoIpResolver> _logger;
    private readonly Lock _reloadGate = new();

    private volatile Database? _current;
    private Database? _retired;
    private bool _disposed;

    /// <summary>
    /// Opens the database, or records that it is unavailable and carries on.
    /// </summary>
    /// <param name="options">Geographic options; supply the provider and the file path.</param>
    /// <param name="metrics">Edge instruments; <c>dle_geoip_available</c> is published from here.</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public MaxMindGeoIpResolver(
        IOptions<GeoIpOptions> options,
        EdgeMetrics metrics,
        ILogger<MaxMindGeoIpResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _metrics = metrics;
        _logger = logger;

        // Loading in the constructor rather than lazily is deliberate: the resolver is a singleton
        // resolved during start-up, so the first request never pays for opening the file, and an
        // operator who mistyped the path is told in the start-up log rather than by a silent absence
        // of country data days later.
        _ = Reload();
    }

    /// <inheritdoc />
    public bool IsAvailable => _current is not null;

    /// <summary>Path the resolver was configured with, for diagnostics.</summary>
    public string ConfiguredPath => _options.Path ?? string.Empty;

    /// <inheritdoc />
    /// <remarks>
    /// Returns <see langword="null"/> rather than throwing for every failure mode there is: no
    /// database, an address the database does not cover, a private or loopback address, or a reader
    /// that was disposed by a reload a moment ago. The caller treats all of them the same way.
    /// </remarks>
    public GeoLocation? Resolve(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            return null;
        }

        // A reverse proxy on the same host reaches the edge over ::ffff:10.0.0.7 rather than 10.0.0.7,
        // so the mapped form is unwrapped before anything looks at the ranges below.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        // No loopback, link-local or RFC 1918 address is in any geographic database, and all of them
        // are common in front of a proxy that was not configured to forward the client address.
        // Skipping them turns a guaranteed miss into a branch.
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || IsPrivateV4(address))
        {
            return null;
        }

        Database? database = _current;

        if (database is null)
        {
            return null;
        }

        return Lookup(database, address);
    }

    /// <summary>
    /// Re-reads the database file if it changed on disk, and swaps the reader when it did.
    /// </summary>
    /// <returns><see langword="true"/> when a new reader was installed.</returns>
    /// <remarks>
    /// <para>
    /// Change detection is length plus last-write time. Hashing the file would be exact and would also
    /// read a hundred megabytes off disk on every tick to answer a question that the file system has
    /// already answered; a replacement that preserves both values is not a scenario the MaxMind
    /// distribution produces.
    /// </para>
    /// <para>
    /// The reader being replaced is not disposed here. A request that started before the swap may
    /// still be walking the old mapping, and disposing it underneath would turn a routine refresh
    /// into a burst of failed lookups. It is held in <c>_retired</c> instead and disposed by the
    /// <em>next</em> reload, which is at least one refresh interval later — far longer than any
    /// request lives.
    /// </para>
    /// </remarks>
    public bool Reload()
    {
        if (!string.Equals(_options.Provider, ProviderName, StringComparison.Ordinal))
        {
            EdgeLog.GeoIpDisabled(_logger);
            _metrics.SetGeoIpAvailable(false);
            return false;
        }

        string? path = string.IsNullOrWhiteSpace(_options.Path) ? null : _options.Path.Trim();

        if (path is null)
        {
            EdgeLog.GeoIpUnavailable(_logger, "(unset)");
            _metrics.SetGeoIpAvailable(_current is not null);
            return false;
        }

        lock (_reloadGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            return ReloadCore(path);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_reloadGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _current?.Dispose();
            _retired?.Dispose();
            _current = null;
            _retired = null;
        }
    }

    /// <summary>
    /// Opens the file and installs the reader. Runs under <c>_reloadGate</c>.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "§D.6 requires that a missing or corrupt geographic database degrades to country=null and an " +
                        "alert-worthy metric. Every failure is logged and the branch ends in an explicit denial of " +
                        "geographic data, which satisfies SHARED-KERNEL §17.9.")]
    private bool ReloadCore(string path)
    {
        FileInfo file;

        try
        {
            file = new FileInfo(path);

            if (!file.Exists)
            {
                EdgeLog.GeoIpUnavailable(_logger, path);
                _metrics.SetGeoIpAvailable(_current is not null);
                return false;
            }
        }
        catch (Exception exception)
        {
            EdgeLog.GeoIpLoadFailed(_logger, path, exception);
            _metrics.SetGeoIpAvailable(_current is not null);
            return false;
        }

        Database? existing = _current;

        if (existing is not null && existing.Matches(file))
        {
            _metrics.SetGeoIpAvailable(true);
            return false;
        }

        DatabaseReader? reader = null;

        try
        {
            reader = new DatabaseReader(path, FileAccessMode.MemoryMapped);

            string databaseType = reader.Metadata.DatabaseType;
            var opened = new Database(reader, databaseType, file.Length, file.LastWriteTimeUtc);

            reader = null;

            _retired?.Dispose();
            _retired = existing;
            _current = opened;

            _metrics.SetGeoIpAvailable(true);

            if (existing is null)
            {
                EdgeLog.GeoIpLoaded(_logger, path, databaseType);
            }
            else
            {
                EdgeLog.GeoIpReloaded(_logger, path);
            }

            return true;
        }
        catch (Exception exception)
        {
            reader?.Dispose();

            EdgeLog.GeoIpLoadFailed(_logger, path, exception);

            // The previously loaded database, if any, keeps serving. A replacement that turned out to
            // be truncated must not take working geographic data away with it.
            _metrics.SetGeoIpAvailable(_current is not null);
            return false;
        }
    }

    /// <summary>Performs one lookup, turning every failure into "unknown".</summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A geographic lookup must never fail a redirect (§D.6). The branch ends in a null location, " +
                        "which is the explicit deny-by-default outcome required by SHARED-KERNEL §17.9.")]
    private GeoLocation? Lookup(Database database, IPAddress address)
    {
        try
        {
            if (database.IsCityDatabase)
            {
                if (!database.Reader.TryCity(address, out CityResponse? city) || city is null)
                {
                    return null;
                }

                return new GeoLocation
                {
                    Country = city.Country.IsoCode,
                    Region = city.MostSpecificSubdivision.IsoCode,
                    City = city.City.Name,
                };
            }

            if (!database.Reader.TryCountry(address, out CountryResponse? country) || country is null)
            {
                return null;
            }

            return new GeoLocation { Country = country.Country.IsoCode };
        }
        catch (Exception exception)
        {
            LookupFailed(_logger, exception);
            return null;
        }
    }

    /// <summary>
    /// Whether an IPv4 address is in one of the RFC 1918 ranges or the RFC 6598 shared range.
    /// </summary>
    private static bool IsPrivateV4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[4];

        if (!address.TryWriteBytes(bytes, out int written) || written != 4)
        {
            return false;
        }

        return bytes[0] switch
        {
            10 => true,
            127 => true,
            169 when bytes[1] == 254 => true,
            172 when bytes[1] >= 16 && bytes[1] <= 31 => true,
            192 when bytes[1] == 168 => true,
            100 when bytes[1] >= 64 && bytes[1] <= 127 => true,
            _ => false,
        };
    }

    [LoggerMessage(
        EventId = 1015,
        Level = LogLevel.Debug,
        Message = "Geographic lookup failed; the request is treated as having no geographic data.")]
    private static partial void LookupFailed(ILogger logger, Exception exception);

    /// <summary>
    /// One opened database file: the reader plus the identity of the file it was opened from.
    /// </summary>
    private sealed class Database : IDisposable
    {
        private readonly long _length;
        private readonly DateTime _lastWriteUtc;

        internal Database(DatabaseReader reader, string databaseType, long length, DateTime lastWriteUtc)
        {
            Reader = reader;

            // GeoLite2-City and GeoIP2-City expose subdivisions and city names; the Country editions do
            // not, and asking one of them for a city throws rather than returning nothing. The database
            // type is read once here so that the hot path is a boolean test.
            IsCityDatabase = databaseType.Contains("City", StringComparison.OrdinalIgnoreCase);

            _length = length;
            _lastWriteUtc = lastWriteUtc;
        }

        internal DatabaseReader Reader { get; }

        internal bool IsCityDatabase { get; }

        internal bool Matches(FileInfo file) =>
            file.Length == _length && file.LastWriteTimeUtc == _lastWriteUtc;

        public void Dispose() => Reader.Dispose();
    }
}
