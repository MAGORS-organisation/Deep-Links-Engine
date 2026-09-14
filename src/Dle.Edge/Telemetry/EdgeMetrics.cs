using System.Diagnostics.Metrics;

namespace Dle.Edge.Telemetry;

/// <summary>
/// The instrument set of §C.6 that the edge owns.
/// </summary>
/// <remarks>
/// <para>
/// Instrument names use the OpenTelemetry dotted spelling; a Prometheus exporter renders
/// <c>dle.resolve.duration</c> as <c>dle_resolve_duration_seconds</c> and the counter
/// <c>dle.resolve</c> as <c>dle_resolve_total</c>, which are the names §C.6 puts dashboards and
/// alerts on. <c>dle_click_events_dropped_total</c> is not here — it belongs to the bounded channel
/// in <c>Dle.Persistence.Fast</c>, and the telemetry module adds that meter to the same pipeline
/// rather than counting the same event twice.
/// </para>
/// <para>
/// The cache ratio is split into L1 and L2 as §C.6 requires, and the split is measured rather than
/// guessed: <c>HybridCache</c> only invokes a serializer when a value crosses the distributed level,
/// so a deserialization is exactly an L2 read and everything else that skipped the factory was an L1
/// hit. <see cref="RecordDistributedRead"/> is called from the cache serializer for that reason.
/// </para>
/// </remarks>
public sealed class EdgeMetrics : IDisposable
{
    /// <summary>Meter name to register with the OpenTelemetry metrics provider.</summary>
    public const string MeterName = "Dle.Edge";

    private readonly Meter _meter;
    private readonly Histogram<double> _resolveDuration;
    private readonly Counter<long> _resolveTotal;
    private readonly Counter<long> _shadowBanned;
    private readonly DomainVerificationFailureRegistry _domainVerification;

    private long _lookups;
    private long _factoryRuns;
    private long _distributedReads;
    private long _geoIpAvailable;

    /// <summary>Creates the instruments.</summary>
    /// <param name="meterFactory">
    /// Meter factory supplied by the host, so a test can observe the instruments through
    /// <c>MetricCollector</c>. When it is <see langword="null"/> the meter is created directly, so the
    /// instruments still reach any listener.
    /// </param>
    /// <param name="domainVerification">
    /// Registry of hosts whose association files currently fail validation, observed by
    /// <c>dle_domain_verification_failures</c>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="domainVerification"/> is <see langword="null"/>.</exception>
    public EdgeMetrics(IMeterFactory? meterFactory, DomainVerificationFailureRegistry domainVerification)
    {
        ArgumentNullException.ThrowIfNull(domainVerification);

        _domainVerification = domainVerification;
        _meter = meterFactory is null ? new Meter(MeterName) : meterFactory.Create(MeterName);

        _resolveDuration = _meter.CreateHistogram<double>(
            "dle.resolve.duration",
            unit: "s",
            description: "Wall-clock time spent resolving one link, tagged with outcome, cache level and channel.");

        _resolveTotal = _meter.CreateCounter<long>(
            "dle.resolve",
            unit: "{request}",
            description: "Resolve requests, tagged with decision, platform and whether the client was a verified bot.");

        _shadowBanned = _meter.CreateCounter<long>(
            "dle.resolve.shadow_banned",
            unit: "{request}",
            description: "Resolve requests answered from the enumeration shadow ban without a database lookup (T-07).");

        _ = _meter.CreateObservableGauge(
            "dle.cache.hit_ratio",
            ObserveCacheHitRatio,
            unit: "1",
            description: "Share of link lookups served by each cache level, L1 and L2 reported separately (§C.6).");

        _ = _meter.CreateObservableGauge(
            "dle.domain_verification.failures",
            () => new Measurement<long>(_domainVerification.Count),
            unit: "{domain}",
            description: "Hosts whose Apple or Android association file currently fails validation. Alert on any non-zero value (§C.6).");

        _ = _meter.CreateObservableGauge(
            "dle.geoip.available",
            () => new Measurement<long>(Interlocked.Read(ref _geoIpAvailable)),
            unit: "1",
            description: "1 when the geographic database is loaded, 0 when it is missing or corrupt and country is null (§D.6).");
    }

    /// <summary>Records one completed resolve.</summary>
    /// <param name="seconds">Elapsed time in seconds.</param>
    /// <param name="outcome">Coarse result, one of <see cref="ResolveOutcomes"/>.</param>
    /// <param name="cache">Cache level that answered the lookup, one of <see cref="CacheLevels"/>.</param>
    /// <param name="channel">Client channel name from <see cref="ChannelNames"/>.</param>
    /// <param name="decision">Click stream decision name from <see cref="DecisionNames"/>.</param>
    /// <param name="platform">Client platform.</param>
    /// <param name="isBot">Whether the client was a verified crawler.</param>
    public void RecordResolve(
        double seconds,
        string outcome,
        string cache,
        string channel,
        string decision,
        Platform platform,
        bool isBot)
    {
        _resolveDuration.Record(
            seconds,
            new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("cache", cache),
            new KeyValuePair<string, object?>("channel", channel));

        _resolveTotal.Add(
            1,
            new KeyValuePair<string, object?>("decision", decision),
            new KeyValuePair<string, object?>("platform", PlatformName(platform)),
            new KeyValuePair<string, object?>("is_bot", isBot));
    }

    /// <summary>Counts one request answered from the shadow ban list.</summary>
    public void RecordShadowBanned() => _shadowBanned.Add(1);

    /// <summary>Counts one link lookup, whichever level ends up answering it.</summary>
    public void RecordLookup() => Interlocked.Increment(ref _lookups);

    /// <summary>Counts one lookup that reached the database because no cache level had the entry.</summary>
    public void RecordFactoryRun() => Interlocked.Increment(ref _factoryRuns);

    /// <summary>
    /// Counts one value read back from the shared cache level. Called by the cache serializer, which
    /// is the only place that can tell an L2 read from an L1 read.
    /// </summary>
    public void RecordDistributedRead() => Interlocked.Increment(ref _distributedReads);

    /// <summary>Publishes whether the geographic database is currently usable.</summary>
    /// <param name="available"><see langword="true"/> when lookups can succeed.</param>
    public void SetGeoIpAvailable(bool available) => Interlocked.Exchange(ref _geoIpAvailable, available ? 1 : 0);

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();

    /// <summary>Canonical lowercase platform tag values.</summary>
    private static string PlatformName(Platform platform) => platform switch
    {
        Platform.Ios => "ios",
        Platform.Android => "android",
        Platform.Desktop => "desktop",
        Platform.Other => "other",
        _ => "unknown",
    };

    private IEnumerable<Measurement<double>> ObserveCacheHitRatio()
    {
        long lookups = Interlocked.Read(ref _lookups);
        long misses = Interlocked.Read(ref _factoryRuns);
        long l2 = Interlocked.Read(ref _distributedReads);

        if (lookups <= 0)
        {
            yield return new Measurement<double>(0d, new KeyValuePair<string, object?>("level", CacheLevels.Local));
            yield return new Measurement<double>(0d, new KeyValuePair<string, object?>("level", CacheLevels.Distributed));
            yield break;
        }

        // A lookup that neither ran the factory nor deserialized a payload was answered from the
        // in-process level. Clamping matters because the three counters are read without a lock and a
        // sample taken mid-request can otherwise produce a ratio just outside [0, 1].
        double total = lookups;
        double distributed = Math.Clamp(l2 / total, 0d, 1d);
        double local = Math.Clamp((lookups - misses - l2) / total, 0d, 1d);

        yield return new Measurement<double>(local, new KeyValuePair<string, object?>("level", CacheLevels.Local));
        yield return new Measurement<double>(distributed, new KeyValuePair<string, object?>("level", CacheLevels.Distributed));
    }
}

/// <summary>Values of the <c>cache</c> tag on <c>dle_resolve_duration_seconds</c>.</summary>
public static class CacheLevels
{
    /// <summary>Answered from the in-process cache.</summary>
    public const string Local = "l1";

    /// <summary>Answered from the shared Valkey cache.</summary>
    public const string Distributed = "l2";

    /// <summary>Answered from the database because no cache level had the entry.</summary>
    public const string Miss = "miss";

    /// <summary>No lookup happened, so no level answered it.</summary>
    public const string None = "none";
}

/// <summary>Values of the <c>outcome</c> tag on <c>dle_resolve_duration_seconds</c>.</summary>
public static class ResolveOutcomes
{
    /// <summary>A redirect was issued.</summary>
    public const string Redirect = "redirect";

    /// <summary>An interstitial page was rendered.</summary>
    public const string Interstitial = "interstitial";

    /// <summary>An Open Graph preview was rendered for a crawler.</summary>
    public const string Preview = "preview";

    /// <summary>The link does not exist, is not active yet, or belongs to another host.</summary>
    public const string NotFound = "not_found";

    /// <summary>The link was withdrawn.</summary>
    public const string Gone = "gone";

    /// <summary>A routing rule blocked this client.</summary>
    public const string Blocked = "blocked";

    /// <summary>The request was rejected by a rate limiter.</summary>
    public const string RateLimited = "rate_limited";

    /// <summary>The lookup could not be completed because a dependency was unavailable.</summary>
    public const string Unavailable = "unavailable";
}
