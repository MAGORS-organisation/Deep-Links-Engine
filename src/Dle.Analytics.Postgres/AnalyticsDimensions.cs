using System.Diagnostics.CodeAnalysis;

namespace Dle.Analytics.Postgres;

/// <summary>
/// The closed set of dimensions a breakdown may group by
/// (<see cref="IClickAnalyticsStore.GetBreakdownAsync"/>).
/// </summary>
/// <remarks>
/// The port's own documentation requires implementations to map a dimension name to a column
/// through a fixed allowlist and never to interpolate the caller's string into SQL. This is that
/// allowlist. It is public so the control plane can reject an unknown dimension with a validation
/// error before a query is ever built, rather than turning it into a 500.
/// </remarks>
public static class AnalyticsDimensions
{
    private static readonly Dictionary<string, AnalyticsDimension> ByName = Build();

    /// <summary>Every supported dimension name, in a stable order.</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        "country", "region", "platform", "os_family", "device_class", "channel", "language",
        "referrer_host", "decision", "link", "campaign", "ab_variant",
    ];

    /// <summary>Resolves a dimension name.</summary>
    /// <param name="name">The requested dimension. Case and surrounding whitespace are ignored.</param>
    /// <param name="dimension">The resolved dimension when the name is known.</param>
    /// <returns><see langword="true"/> when the name is on the allowlist.</returns>
    public static bool TryResolve(
        string? name,
        [NotNullWhen(true)] out AnalyticsDimension? dimension)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            dimension = null;
            return false;
        }

        return ByName.TryGetValue(name.Trim().ToLowerInvariant(), out dimension);
    }

    private static Dictionary<string, AnalyticsDimension> Build()
    {
        // Rows whose dimension is unknown are reported under the literal key "unknown" rather than
        // dropped, so the rows of a breakdown always sum to the totals of the time series.
        AnalyticsDimension[] dimensions =
        [
            new("country", "coalesce(nullif({0}.country, ''), 'unknown')",
                "coalesce(nullif({0}.country, ''), 'unknown')", false),
            new("region", "coalesce(nullif({0}.region, ''), 'unknown')", null, false),
            new("platform", "dle_platform_of({0}.os_family, {0}.device_class)",
                "coalesce(nullif({0}.platform, ''), 'unknown')", false),
            new("os_family", "coalesce(nullif({0}.os_family, ''), 'unknown')", null, false),
            new("device_class", "coalesce(nullif({0}.device_class, ''), 'unknown')", null, false),
            new("channel", "coalesce(nullif({0}.channel, ''), 'unknown')",
                "coalesce(nullif({0}.channel, ''), 'unknown')", false),
            new("language", "coalesce(nullif({0}.language, ''), 'unknown')", null, false),
            new("referrer_host", "coalesce(nullif({0}.referrer_host, ''), 'unknown')", null, false),
            new("decision", "coalesce(nullif({0}.decision, ''), 'unknown')", null, false),
            new("link", "{0}.link_id::text", "{0}.link_id::text", false),
            new("campaign", "coalesce(l.campaign_id::text, 'unknown')",
                "coalesce({0}.campaign_id::text, 'unknown')", true),
            new("ab_variant", "coalesce({0}.ab_bucket::text, 'unknown')", null, false),
        ];

        Dictionary<string, AnalyticsDimension> map = new(dimensions.Length, StringComparer.Ordinal);

        foreach (AnalyticsDimension dimension in dimensions)
        {
            map[dimension.Name] = dimension;
        }

        return map;
    }
}
