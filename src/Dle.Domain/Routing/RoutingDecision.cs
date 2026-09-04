namespace Dle.Domain.Routing;

/// <summary>
/// The outcome of evaluating a rule set for one client. It is a pure value: the edge turns it into an
/// HTTP response, and <see cref="RoutingUrlBuilder"/> turns it into concrete URLs (ADR-009).
/// </summary>
public sealed record RoutingDecision
{
    /// <summary>The response class the edge has to produce.</summary>
    public required DecisionKind Kind { get; init; }

    /// <summary>
    /// Identifier of the rule that matched. Empty for the terminal decisions
    /// <see cref="NotFound"/> and <see cref="Gone"/>, which are reached without evaluating any rule.
    /// </summary>
    public required string MatchedRuleId { get; init; }

    /// <summary>The action declared by the matched rule, before it was resolved into <see cref="Kind"/>.</summary>
    public required RoutingActionKind Action { get; init; }

    /// <summary>In-app path from the matched rule, or <see langword="null"/> to fall back to the link's own path.</summary>
    public string? DeeplinkPath { get; init; }

    /// <summary>Web URL from the matched rule, or <see langword="null"/> to fall back to the link's target URL.</summary>
    public string? WebUrl { get; init; }

    /// <summary>Store URL from the matched rule, or <see langword="null"/> to fall back to the link's per-platform store URL.</summary>
    public string? StoreUrl { get; init; }

    /// <summary>
    /// Play Install Referrer template from the matched rule (§A.2.4). Pairs that carry the
    /// <c>{click_id}</c> placeholder are already removed here when consent does not permit click-id
    /// linking, so no consumer of this decision can leak the click id.
    /// </summary>
    public string? ReferrerTemplate { get; init; }

    /// <summary>Interstitial override taken from the matched rule.</summary>
    public InterstitialMode Interstitial { get; init; }

    /// <summary>Name of the A/B variant that matched, or <see langword="null"/> when the rule had no split.</summary>
    public string? AbVariant { get; init; }

    /// <summary>
    /// The deterministic bucket 0..99 the click id fell into, reported only when the matched rule
    /// actually declared an A/B split. See <see cref="ConsistentBucket.Of(string)"/>.
    /// </summary>
    public short? AbBucket { get; init; }

    /// <summary>
    /// No rule matched, or the link is not servable. The edge answers <c>404</c> — the same response and the
    /// same timing as for a link belonging to another tenant, so that no enumeration oracle exists (TC-102, TC-166).
    /// </summary>
    public static RoutingDecision NotFound { get; } = new()
    {
        Kind = DecisionKind.NotFound,
        MatchedRuleId = string.Empty,
        Action = RoutingActionKind.Block,
    };

    /// <summary>The link existed but has been withdrawn. The edge answers <c>410</c> with an explanatory page (TC-103).</summary>
    public static RoutingDecision Gone { get; } = new()
    {
        Kind = DecisionKind.Gone,
        MatchedRuleId = string.Empty,
        Action = RoutingActionKind.Block,
    };

    /// <summary>
    /// A rule explicitly refused to serve this client — geo blocking, abuse containment or a kill switch.
    /// </summary>
    /// <param name="ruleId">Identifier of the rule that produced the block; written to the click stream.</param>
    /// <returns>A decision with <see cref="DecisionKind.Blocked"/>.</returns>
    public static RoutingDecision Blocked(string ruleId) => new()
    {
        Kind = DecisionKind.Blocked,
        MatchedRuleId = ruleId ?? string.Empty,
        Action = RoutingActionKind.Block,
    };
}
