namespace Dle.Domain.Routing;

/// <summary>
/// One arm of a deterministic A/B split (FR-125).
/// </summary>
/// <remarks>
/// Variants of a single rule are laid out as consecutive percentage ranges over the
/// bucket space 0..99 produced by <see cref="ConsistentBucket"/>. The sum of all
/// <see cref="Percent"/> values may be lower than 100; a bucket beyond the accumulated
/// total simply does not match the rule and evaluation continues with the next rule.
/// </remarks>
public sealed record AbVariant
{
    /// <summary>Variant name reported in analytics, for example <c>"a"</c> or <c>"control"</c>. Unique within a rule.</summary>
    public required string Variant { get; init; }

    /// <summary>Share of traffic assigned to this variant, 1..100. The sum over one rule must not exceed 100.</summary>
    public required int Percent { get; init; }
}
