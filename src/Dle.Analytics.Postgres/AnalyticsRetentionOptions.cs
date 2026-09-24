using System.ComponentModel.DataAnnotations;

namespace Dle.Analytics.Postgres;

/// <summary>
/// Retention policy of the analytics data, bound from <c>Dle:Privacy:Retention</c>
/// (SHARED-KERNEL §16, FR-247, §E.6.3).
/// </summary>
/// <remarks>
/// The defaults are the ones the specification names: thirty days of raw, hash-only click stream
/// and two years of aggregates. Raw events are never stored with an IP address in the first place
/// — <c>ip_hash</c> is an HMAC under a daily rotating salt, and <c>ip_prefix</c> exists only under
/// consent = full — so retention here is about deleting rows, not about de-identifying them after
/// the fact, which §E.6.2 rejects as "write it now and erase it later".
/// </remarks>
public sealed class AnalyticsRetentionOptions : IValidatableObject
{
    /// <summary>Name of the configuration section this type is bound from.</summary>
    public const string SectionName = "Dle:Privacy:Retention";

    /// <summary>
    /// Days the raw click stream is kept. Partitions of <c>click_events</c> whose entire range is
    /// older than this are detached and dropped.
    /// </summary>
    [Range(1, 3650)]
    public int RawDays { get; set; } = 30;

    /// <summary>Days the rollup tables are kept. Aggregates outlive the raw events they came from.</summary>
    [Range(1, 36500)]
    public int AggregatedDays { get; set; } = 730;

    /// <summary>
    /// Optional: days after which <c>ip_prefix</c> is cleared from raw events still inside the raw
    /// window, leaving them hash-only. <see langword="null"/> disables the pass, in which case the
    /// prefix simply disappears with its partition after <see cref="RawDays"/>.
    /// </summary>
    [Range(1, 3650)]
    public int? IpPrefixDays { get; set; }

    /// <summary>
    /// When set, the retention job computes and audits what it would remove but removes nothing.
    /// Meant for the first run after a policy change.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Maximum number of partitions one retention pass may drop. Bounds the catalogue churn of the
    /// first run against a database that has never had retention applied.
    /// </summary>
    [Range(1, 4096)]
    public int MaxPartitionsPerRun { get; set; } = 32;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (AggregatedDays < RawDays)
        {
            yield return new ValidationResult(
                "Dle:Privacy:Retention:AggregatedDays must be at least RawDays: aggregates are "
                + "derived from raw events and must not expire before them.",
                [nameof(AggregatedDays)]);
        }

        if (IpPrefixDays is { } prefixDays && prefixDays > RawDays)
        {
            yield return new ValidationResult(
                "Dle:Privacy:Retention:IpPrefixDays must not exceed RawDays: a pass that only "
                + "runs after the rows are already gone is not a privacy control.",
                [nameof(IpPrefixDays)]);
        }
    }
}
