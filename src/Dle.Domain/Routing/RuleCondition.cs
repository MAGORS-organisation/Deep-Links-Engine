namespace Dle.Domain.Routing;

/// <summary>
/// The <c>when</c> half of a routing rule (§B.5.4, FR-121…FR-126).
/// </summary>
/// <remarks>
/// <para>
/// Every property is optional. A property left <see langword="null"/> (or set to an empty array)
/// means "do not care" and is skipped during evaluation. Within a single property the listed
/// values are OR-ed; across properties the results are AND-ed. A condition with no property set
/// at all therefore matches every client — but the canonical way to express the catch-all rule is
/// <see cref="RoutingRule.When"/> being <see langword="null"/>, which is what
/// <see cref="RoutingRuleValidator"/> recognises as the mandatory default rule.
/// </para>
/// <para>
/// Comparisons are culture independent: platforms and channels are matched against their canonical
/// lowercase names, countries against ISO-3166-1 alpha-2 codes, and languages against the primary
/// subtag only, so a rule that lists <c>"sk"</c> also matches a client that announced <c>sk-SK</c>.
/// </para>
/// </remarks>
public sealed record RuleCondition
{
    /// <summary>
    /// Platforms the rule applies to, as lowercase enum names:
    /// <c>"ios"</c>, <c>"android"</c>, <c>"desktop"</c>, <c>"other"</c>, <c>"unknown"</c>.
    /// </summary>
    public string[]? Platform { get; init; }

    /// <summary>Constraint on the operating system version reported by the client (FR-124).</summary>
    public VersionPredicate? OsVersion { get; init; }

    /// <summary>Constraint on the host application version reported by the SDK (FR-124).</summary>
    public VersionPredicate? AppVersion { get; init; }

    /// <summary>Countries the rule applies to, as ISO-3166-1 alpha-2 codes in upper case, for example <c>"SK"</c> (FR-122).</summary>
    public string[]? Country { get; init; }

    /// <summary>Sub-national regions the rule applies to, in the notation produced by the GeoIP provider (FR-122).</summary>
    public string[]? Region { get; init; }

    /// <summary>Languages the rule applies to, as lowercase primary subtags, for example <c>"sk"</c> (FR-123).</summary>
    public string[]? Language { get; init; }

    /// <summary>Client channels the rule applies to, using the canonical names in <see cref="ChannelNames"/>.</summary>
    public string[]? Channel { get; init; }

    /// <summary>Time constraint evaluated in UTC (FR-126).</summary>
    public TimeWindowPredicate? TimeWindow { get; init; }

    /// <summary>
    /// Deterministic A/B split (FR-125). The variants are laid out as consecutive percentage ranges over the
    /// bucket space produced by <see cref="ConsistentBucket.Of(string)"/>; if the bucket falls beyond the
    /// accumulated total, the rule does not match and evaluation continues with the next rule.
    /// </summary>
    public AbVariant[]? Ab { get; init; }
}
