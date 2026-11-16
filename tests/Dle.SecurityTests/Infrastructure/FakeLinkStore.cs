using System.Collections.Concurrent;
using System.Collections.ObjectModel;

namespace Dle.SecurityTests.Infrastructure;

/// <summary>
/// An in-memory <see cref="ILinkStore"/> keyed exactly the way the real one is: by host and slug.
/// </summary>
/// <remarks>
/// <para>
/// Substituting the store rather than standing up PostgreSQL is deliberate and is not a compromise for
/// this suite. Every property these tests assert — that a <c>Location</c> header can never name an
/// attacker's host, that a miss and a foreign link are indistinguishable, that the enumeration budget
/// is separate from the success budget — is a property of the resolve pipeline and the routing rules,
/// not of the query that fetched the row. The database's own guarantees (row level security, the
/// tenant filter, the covering index) belong to the integration suite, which needs a real server.
/// </para>
/// <para>
/// The store counts its lookups, which is how the shadow-ban tests prove that a banned prefix is
/// answered without a store read at all.
/// </para>
/// </remarks>
public sealed class FakeLinkStore : ILinkStore
{
    private readonly ConcurrentDictionary<string, LinkSnapshot> _links = new(StringComparer.Ordinal);
    private long _lookups;

    /// <summary>How many times the store was actually asked for a link.</summary>
    public long Lookups => Interlocked.Read(ref _lookups);

    /// <summary>Adds or replaces a link.</summary>
    /// <param name="host">The host the link lives on.</param>
    /// <param name="link">The snapshot.</param>
    /// <returns>The same snapshot, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="link"/> is <see langword="null"/>.</exception>
    public LinkSnapshot Add(string host, LinkSnapshot link)
    {
        ArgumentNullException.ThrowIfNull(link);

        _links[Key(host, link.Slug)] = link;

        return link;
    }

    /// <summary>Forgets every link and resets the lookup counter.</summary>
    public void Clear()
    {
        _links.Clear();
        Interlocked.Exchange(ref _lookups, 0);
    }

    /// <inheritdoc />
    public ValueTask<LinkSnapshot?> FindAsync(string host, string slug, CancellationToken ct)
    {
        Interlocked.Increment(ref _lookups);

        return ValueTask.FromResult(_links.TryGetValue(Key(host, slug), out LinkSnapshot? link) ? link : null);
    }

    /// <summary>
    /// Builds a servable link whose default rule redirects to <paramref name="targetUrl"/>.
    /// </summary>
    /// <param name="slug">The slug.</param>
    /// <param name="targetUrl">The web target the default rule names.</param>
    /// <param name="tenantId">Owning tenant; defaults to a fixed identifier.</param>
    /// <returns>The snapshot.</returns>
    public static LinkSnapshot WebLink(string slug, string targetUrl, Guid? tenantId = null) => new()
    {
        Id = 7286414500000000001,
        TenantId = tenantId ?? new Guid("11111111-1111-1111-1111-111111111111"),
        DomainId = new Guid("22222222-2222-2222-2222-222222222222"),
        Slug = slug,
        TargetUrl = targetUrl,
        RoutingRules =
        [
            new RoutingRule
            {
                Id = "default",
                Then = new RuleAction { Action = RoutingActionKind.Web, Url = targetUrl },
            },
        ],
        Og = new OgMeta(),
        Utm = ReadOnlyDictionary<string, string>.Empty,
        IsActive = true,
        TenantConsentMode = ConsentMode.AggregateOnly,
    };

    /// <summary>Builds a link whose rule always renders the interstitial page.</summary>
    /// <param name="slug">The slug.</param>
    /// <param name="targetUrl">The web fallback shown on the page.</param>
    /// <param name="storeUrl">The store listing the page's button points at.</param>
    /// <returns>The snapshot.</returns>
    public static LinkSnapshot InterstitialLink(string slug, string targetUrl, string storeUrl)
    {
        LinkSnapshot link = WebLink(slug, targetUrl);

        return link with
        {
            AndroidStoreUrl = storeUrl,
            IosStoreUrl = storeUrl,
            DeeplinkPath = "/promo/autumn",
            RoutingRules =
            [
                new RoutingRule
                {
                    Id = "default",
                    Then = new RuleAction
                    {
                        Action = RoutingActionKind.AppOrStore,
                        Url = targetUrl,
                        StoreUrl = storeUrl,
                        DeeplinkPath = "/promo/autumn",
                        Interstitial = InterstitialMode.Always,
                    },
                },
            ],
        };
    }

    private static string Key(string host, string slug) =>
        host.ToUpperInvariant() + "\0" + slug;
}
