namespace Dle.Domain.Routing;

/// <summary>
/// Evaluates a link's rule set against one classified client.
/// </summary>
/// <remarks>
/// The evaluation is deterministic and allocation-light: it is on the resolve hot path, whose whole
/// budget for rules is 0,3 ms (§B.6.1). It performs no I/O and never consults the incoming request's
/// query string for a target (TC-164).
/// </remarks>
public interface IRoutingEngine
{
    /// <summary>
    /// Evaluates the rules in order; the first rule that matches wins (FR-127). If nothing matches,
    /// the default rule — the one whose <see cref="RoutingRule.When"/> is <see langword="null"/> — applies.
    /// If even that is missing, which validation must prevent (TC-105), the result is
    /// <see cref="RoutingDecision.NotFound"/>.
    /// </summary>
    /// <param name="rules">The link's rule set, in author order. May be empty.</param>
    /// <param name="client">The classified client: platform, channel, geo, language, versions.</param>
    /// <param name="consent">
    /// The consent decision produced by the consent gate. Consent is an input to routing, not a filter
    /// applied afterwards: when it does not permit click-id linking, the resulting decision already has
    /// every click-id-bearing part of the referrer template removed (§B.1/5, §E.6.2).
    /// </param>
    /// <param name="clickId">
    /// The click identifier for this request. It seeds the deterministic A/B bucket
    /// <see cref="ConsistentBucket.Of(string)"/> ∈ 0..99 (FR-125); it is never used as a target.
    /// </param>
    /// <returns>The decision the edge turns into an HTTP response.</returns>
    RoutingDecision Evaluate(
        IReadOnlyList<RoutingRule> rules,
        ClientContext client,
        ConsentDecision consent,
        string clickId);
}
