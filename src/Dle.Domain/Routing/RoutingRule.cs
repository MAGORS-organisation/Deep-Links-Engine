namespace Dle.Domain.Routing;

/// <summary>
/// One routing rule (§B.5.4). Rules are data, never code: they are evaluated deterministically,
/// in order, and the first match wins (FR-127).
/// </summary>
public sealed record RoutingRule
{
    /// <summary>
    /// Stable identifier of the rule, unique within the link. It is written to the click stream as
    /// <see cref="RoutingDecision.MatchedRuleId"/>, so renaming it breaks historical reports.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The match condition. <see langword="null"/> marks the default rule, which matches every client.
    /// A rule set must contain exactly one such rule and it must be the last one; a set without it
    /// cannot be saved (FR-127, TC-105).
    /// </summary>
    public RuleCondition? When { get; init; }

    /// <summary>What to do once the rule matches.</summary>
    public required RuleAction Then { get; init; }
}
