namespace Dle.Domain.Routing;

/// <summary>
/// What a routing rule does once it has matched (§B.5.4).
/// </summary>
/// <remarks>
/// The wire format is snake_case, produced by the shared naming policy:
/// <c>web</c>, <c>app_or_store</c>, <c>store_only</c>, <c>app_only</c>, <c>block</c>.
/// </remarks>
public enum RoutingActionKind
{
    /// <summary>Send the client to a plain web URL. <see cref="RuleAction.Url"/> is mandatory.</summary>
    Web = 0,

    /// <summary>
    /// Try the application first and fall back to the store. The concrete response shape
    /// (interstitial, store redirect or web redirect) is resolved per client by ADR-009.
    /// <see cref="RuleAction.StoreUrl"/> is mandatory.
    /// </summary>
    AppOrStore = 1,

    /// <summary>Always send the client to the store. <see cref="RuleAction.StoreUrl"/> is mandatory.</summary>
    StoreOnly = 2,

    /// <summary>Only open the installed application; never offer the store.</summary>
    AppOnly = 3,

    /// <summary>Serve nothing. Used for geo blocking, abuse containment and kill switches.</summary>
    Block = 4,
}
