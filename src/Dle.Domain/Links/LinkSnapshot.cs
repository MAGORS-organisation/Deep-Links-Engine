namespace Dle.Domain.Links;

/// <summary>
/// Immutable projection of everything the edge needs to answer one click, in one object.
/// </summary>
/// <remarks>
/// <para>
/// This is the unit of caching: it is read from the L1 in-process cache, and on a miss from the L2
/// Valkey cache, and only then from Postgres (§B.6.1). Because it is serialized into L2 it has to be
/// covered by the source-generated JSON context <c>DleDomainJsonContext</c> — reflection-based
/// serialization is forbidden on this path (SHARED-KERNEL §17.3).
/// </para>
/// <para>
/// It is a snapshot, not an entity: nothing here is ever written back. A change in the control plane
/// produces a new snapshot and an explicit cache invalidation.
/// </para>
/// </remarks>
public sealed record LinkSnapshot
{
    /// <summary>Database identity of the link.</summary>
    public required long Id { get; init; }

    /// <summary>Owning tenant. Every lookup is scoped by it; a cross-tenant hit answers <c>404</c>, never <c>403</c> (TC-166).</summary>
    public required Guid TenantId { get; init; }

    /// <summary>The link domain this slug lives under. Slugs are unique per domain, not globally.</summary>
    public required Guid DomainId { get; init; }

    /// <summary>The normalized slug, as it appears in the short URL.</summary>
    public required string Slug { get; init; }

    /// <summary>Absolute web target used when no rule supplies its own URL.</summary>
    public required string TargetUrl { get; init; }

    /// <summary>Default in-app path, used when the matched rule does not supply one.</summary>
    public string? DeeplinkPath { get; init; }

    /// <summary>The rule set, in author order. Evaluated first-match-wins by <see cref="IRoutingEngine"/> (FR-127).</summary>
    public required IReadOnlyList<RoutingRule> RoutingRules { get; init; }

    /// <summary>Open Graph metadata for crawler previews and the interstitial page.</summary>
    public required OgMeta Og { get; init; }

    /// <summary>UTM defaults merged into the web target; parameters already on the target URL win.</summary>
    public required IReadOnlyDictionary<string, string> Utm { get; init; }

    /// <summary>Human readable name shown in the UI and returned by the resolve API.</summary>
    public string? Title { get; init; }

    /// <summary>Campaign this link belongs to, if any.</summary>
    public Guid? CampaignId { get; init; }

    /// <summary>Whether the link is switched on at all.</summary>
    public required bool IsActive { get; init; }

    /// <summary>Start of the link's validity window; before it the link is <see cref="LinkServeState.NotYetActive"/>.</summary>
    public DateTimeOffset? StartsAt { get; init; }

    /// <summary>Exclusive end of the link's validity window; from it the link is <see cref="LinkServeState.Expired"/>.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Where to send a client that arrives after expiry (TC-104). When absent, the edge answers <c>404</c>.</summary>
    public string? ExpiredUrl { get; init; }

    /// <summary>When the link was withdrawn by abuse handling. Any value here makes the link <see cref="LinkServeState.Gone"/>.</summary>
    public DateTimeOffset? QuarantinedAt { get; init; }

    /// <summary>The tenant's consent mode; the upper bound for the effective mode.</summary>
    public required ConsentMode TenantConsentMode { get; init; }

    /// <summary>Per-domain consent override. It can only ever narrow <see cref="TenantConsentMode"/>, never widen it.</summary>
    public ConsentMode? DomainConsentMode { get; init; }

    /// <summary>iOS custom URI scheme used as the last-resort deep link fallback (§A.2.3).</summary>
    public string? IosCustomScheme { get; init; }

    /// <summary>Android custom URI scheme used as the last-resort deep link fallback (§A.2.3).</summary>
    public string? AndroidCustomScheme { get; init; }

    /// <summary>App Store URL used when the matched rule does not supply one.</summary>
    public string? IosStoreUrl { get; init; }

    /// <summary>Play Store URL used when the matched rule does not supply one.</summary>
    public string? AndroidStoreUrl { get; init; }

    /// <summary>
    /// Decides whether the link may be served at a given instant.
    /// </summary>
    /// <param name="now">The current instant, from the request or from a <see cref="TimeProvider"/>.</param>
    /// <returns>The serve state.</returns>
    /// <remarks>
    /// The order of the checks is part of the contract and is load bearing. Quarantine is evaluated
    /// first, so a withdrawn link answers <c>410</c> even if it is also expired or deactivated: abuse
    /// handling must be visible rather than hidden behind a generic <c>404</c> (TC-103). Deactivation
    /// comes next and is deliberately indistinguishable from a missing link.
    /// </remarks>
    public LinkServeState GetServeState(DateTimeOffset now)
    {
        if (QuarantinedAt is not null)
        {
            return LinkServeState.Gone;
        }

        if (!IsActive)
        {
            return LinkServeState.NotFound;
        }

        if (StartsAt is { } startsAt && now < startsAt)
        {
            return LinkServeState.NotYetActive;
        }

        if (ExpiresAt is { } expiresAt && now >= expiresAt)
        {
            return LinkServeState.Expired;
        }

        return LinkServeState.Servable;
    }

    /// <summary>Shorthand for <see cref="GetServeState"/> returning <see cref="LinkServeState.Servable"/>.</summary>
    /// <param name="now">The current instant.</param>
    /// <returns><see langword="true"/> when the link may be resolved and routed.</returns>
    public bool IsServable(DateTimeOffset now) => GetServeState(now) is LinkServeState.Servable;
}
