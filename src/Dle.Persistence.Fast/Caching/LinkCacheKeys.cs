namespace Dle.Persistence.Fast.Caching;

/// <summary>
/// The cache key and tag vocabulary shared by everything that reads or invalidates the link cache
/// (ADR-005, §C.3.1).
/// </summary>
/// <remarks>
/// <para>
/// Keys and tags only work if the reader and the invalidator spell them identically, and they live in
/// different projects: the edge writes entries, the control plane drops them after an edit. A single
/// literal in one place is the difference between an invalidation that works and one that silently
/// matches nothing — a class of bug that shows up as "the change took ten minutes to appear" and is
/// never reproduced on demand.
/// </para>
/// <para>
/// Every entry is tagged with its host and its tenant, which is what makes the two coarse
/// invalidations of <see cref="ILinkCacheInvalidator"/> possible at all: a domain's well-known files or
/// branding change, or a tenant's consent mode changes, and neither knows which slugs are cached.
/// </para>
/// </remarks>
public static class LinkCacheKeys
{
    /// <summary>Prefix of a cached <see cref="LinkSnapshot"/>.</summary>
    public const string LinkPrefix = "lnk:";

    /// <summary>Prefix of a cached Apple app site association document.</summary>
    public const string AasaPrefix = "aasa:";

    /// <summary>Prefix of a cached Digital Asset Links document.</summary>
    public const string AssetLinksPrefix = "assetlinks:";

    /// <summary>Prefix of a cached <see cref="DomainRuntimeConfig"/>.</summary>
    public const string DomainPrefix = "dom:";

    /// <summary>Prefix of the tag that groups every entry belonging to one host.</summary>
    public const string HostTagPrefix = "host:";

    /// <summary>Prefix of the tag that groups every entry belonging to one tenant.</summary>
    public const string TenantTagPrefix = "tenant:";

    /// <summary>Key of one link's snapshot.</summary>
    /// <param name="host">Normalized host.</param>
    /// <param name="slug">Normalized slug.</param>
    /// <returns>The cache key.</returns>
    public static string Link(string host, string slug) => string.Concat(LinkPrefix, host, ":", slug);

    /// <summary>Key of a host's Apple app site association document.</summary>
    /// <param name="host">Normalized host.</param>
    /// <returns>The cache key.</returns>
    public static string Aasa(string host) => string.Concat(AasaPrefix, host);

    /// <summary>Key of a host's Digital Asset Links document.</summary>
    /// <param name="host">Normalized host.</param>
    /// <returns>The cache key.</returns>
    public static string AssetLinks(string host) => string.Concat(AssetLinksPrefix, host);

    /// <summary>Key of a host's runtime configuration.</summary>
    /// <param name="host">Normalized host.</param>
    /// <returns>The cache key.</returns>
    public static string Domain(string host) => string.Concat(DomainPrefix, host);

    /// <summary>Tag that every entry of one host carries.</summary>
    /// <param name="host">Normalized host.</param>
    /// <returns>The cache tag.</returns>
    public static string HostTag(string host) => string.Concat(HostTagPrefix, host);

    /// <summary>Tag that every entry of one tenant carries.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <returns>The cache tag.</returns>
    public static string TenantTag(Guid tenantId) =>
        string.Concat(TenantTagPrefix, tenantId.ToString("d", CultureInfo.InvariantCulture));
}
