using Dle.Edge.Clients;
using Dle.Edge.Configuration;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Dle.Edge.Health;

/// <summary>
/// Readiness of the resolve path (§D.6, NFR-05).
/// </summary>
/// <remarks>
/// <para>
/// The check deliberately performs no I/O, and in particular does not ask whether PostgreSQL is
/// reachable. §D.6 requires that resolve keeps serving cached links through a database outage, so an
/// instance that reported itself unready during one would be removed from rotation by the orchestrator
/// — turning a degradation the design already survives into a full outage, and doing it to every
/// instance at once. Whether the database is reachable belongs on a dashboard, not on a probe that
/// decides whether traffic is routed here.
/// </para>
/// <para>
/// What it does report is the two conditions that make this instance's answers worse than another's
/// while it stays perfectly able to serve: a geographic database that failed to load, which silently
/// sends every geo-conditioned rule to the default (§D.6), and a click event channel that is dropping
/// telemetry because the writer cannot keep up (NFR-06). Both are <em>degraded</em>, which maps to a
/// 200 response, so the instance keeps serving and the operator still sees it.
/// </para>
/// </remarks>
public sealed class ResolvePipelineHealthCheck : IHealthCheck
{
    /// <summary>Tag that selects this check on the readiness endpoint.</summary>
    public const string ReadyTag = "ready";

    /// <summary>Name this check is registered under.</summary>
    public const string CheckName = "resolve";

    private readonly IGeoIpResolver _geoIp;
    private readonly IClickEventSink _sink;
    private readonly bool _geoIpExpected;

    /// <summary>Creates the check.</summary>
    /// <param name="geoIp">Geographic resolver.</param>
    /// <param name="sink">Bounded click event sink.</param>
    /// <param name="geoIpOptions">Geographic options; say whether a database was configured at all.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public ResolvePipelineHealthCheck(
        IGeoIpResolver geoIp,
        IClickEventSink sink,
        IOptions<GeoIpOptions> geoIpOptions)
    {
        ArgumentNullException.ThrowIfNull(geoIp);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(geoIpOptions);

        _geoIp = geoIp;
        _sink = sink;

        // A deployment that ships no geographic database is a supported configuration, not a fault, so
        // the absence only counts against readiness when one was asked for. Reporting degraded forever
        // because of a deliberate choice trains an operator to ignore the signal.
        _geoIpExpected = string.Equals(
            geoIpOptions.Value.Provider?.Trim(),
            MaxMindGeoIpResolver.ProviderName,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        bool geoAvailable = !_geoIpExpected || _geoIp.IsAvailable;
        long dropped = _sink.DroppedCount;

        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["geoip_configured"] = _geoIpExpected,
            ["geoip_available"] = _geoIp.IsAvailable,
            ["click_events_dropped"] = dropped,
        };

        if (geoAvailable && dropped == 0)
        {
            return Task.FromResult(HealthCheckResult.Healthy("The resolve path is fully operational.", data));
        }

        string description = !geoAvailable && dropped > 0
            ? "The geographic database is unavailable and click events are being dropped."
            : !geoAvailable
                ? "The geographic database is unavailable; geo-conditioned rules fall through to the default rule."
                : "Click events are being dropped because the writer cannot keep up; redirects are unaffected.";

        return Task.FromResult(HealthCheckResult.Degraded(description, exception: null, data));
    }
}
