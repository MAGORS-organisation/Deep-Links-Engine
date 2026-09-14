using System.Diagnostics.Metrics;

namespace Dle.Control.Features.Abuse;

/// <summary>
/// The instruments the abuse module publishes (§C.6).
/// </summary>
/// <remarks>
/// Names use the OpenTelemetry dotted spelling; a Prometheus exporter renders
/// <c>dle.abuse.blocked</c> as <c>dle_abuse_blocked_total</c>, which is the metric §C.6 names.
/// The counter is tagged by the source of the verdict, because "blocked by our own list" and
/// "blocked by URLhaus" answer different operational questions: the first says the operator is
/// working, the second says the feed is.
/// </remarks>
public sealed class AbuseMetrics : IDisposable
{
    /// <summary>Meter name to register with the OpenTelemetry metrics provider.</summary>
    public const string MeterName = "Dle.Control.Abuse";

    private readonly Meter _meter;
    private readonly Counter<long> _blocked;
    private readonly Counter<long> _reports;
    private readonly Counter<long> _quarantines;
    private readonly Counter<long> _lookupFailures;

    /// <summary>Creates the instruments.</summary>
    /// <param name="meterFactory">
    /// Meter factory supplied by the host. When it is <see langword="null"/> the meter is created
    /// directly, so metrics still flow to any listener in a host that never called
    /// <c>AddMetrics</c>.
    /// </param>
    public AbuseMetrics(IMeterFactory? meterFactory)
    {
        _meter = meterFactory is null ? new Meter(MeterName) : meterFactory.Create(MeterName);

        _blocked = _meter.CreateCounter<long>(
            "dle.abuse.blocked",
            unit: "{target}",
            description: "Target URLs refused or withdrawn because a safety check rejected them (§E.3).");

        _reports = _meter.CreateCounter<long>(
            "dle.abuse.reports",
            unit: "{report}",
            description: "Abuse reports accepted through the public form (FR-245, DSA article 16).");

        _quarantines = _meter.CreateCounter<long>(
            "dle.abuse.quarantines",
            unit: "{link}",
            description: "Links withdrawn from service, and links released back into it (TC-103).");

        _lookupFailures = _meter.CreateCounter<long>(
            "dle.abuse.lookup_failures",
            unit: "{lookup}",
            description: "Reputation lookups that could not be completed. A rising rate means the "
                + "engine is judging targets on syntax alone.");
    }

    /// <summary>Counts one target rejected by a safety check.</summary>
    /// <param name="source">Which check rejected it: <c>syntax</c>, <c>private_ip</c>,
    /// <c>urlhaus</c>, <c>blocklist</c> or <c>manual</c>.</param>
    /// <param name="level">The verdict level that was reached.</param>
    public void Blocked(string source, string level) => _blocked.Add(
        1,
        new KeyValuePair<string, object?>("source", source),
        new KeyValuePair<string, object?>("level", level));

    /// <summary>Counts one accepted report.</summary>
    /// <param name="reason">The reported reason.</param>
    /// <param name="matched">Whether the reported URL resolved to a known link. Reported as a tag
    /// rather than withheld, because the reporter is not told and the operator must be.</param>
    public void ReportAccepted(string reason, bool matched) => _reports.Add(
        1,
        new KeyValuePair<string, object?>("reason", reason),
        new KeyValuePair<string, object?>("matched", matched));

    /// <summary>Counts one quarantine decision.</summary>
    /// <param name="action">Either <c>quarantine</c> or <c>release</c>.</param>
    /// <param name="actor">Either <c>operator</c> or <c>system</c>.</param>
    public void QuarantineDecision(string action, string actor) => _quarantines.Add(
        1,
        new KeyValuePair<string, object?>("action", action),
        new KeyValuePair<string, object?>("actor", actor));

    /// <summary>Counts one reputation lookup that failed.</summary>
    /// <param name="source">The provider that failed.</param>
    public void LookupFailed(string source) =>
        _lookupFailures.Add(1, new KeyValuePair<string, object?>("source", source));

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
