using System.Data;
using System.Data.Common;

using Dle.Domain.Routing;
using Dle.Persistence.Fast.Configuration;
using Dle.Persistence.Fast.Data;

namespace Dle.Persistence.Fast.Links;

/// <summary>
/// The single database round trip on the resolve hot path: host and slug to
/// <see cref="LinkSnapshot"/> (ADR-004, §B.6.1).
/// </summary>
/// <remarks>
/// <para>
/// This is the reason <c>Dle.Persistence.Fast</c> exists. EF Core spends hundreds of microseconds per
/// operation on entity materialisation and change tracking, which is most of the resolve budget of
/// NFR-01 spent before any work is done, and its ahead-of-time support is explicitly experimental,
/// which would rule out an AOT edge resolver altogether (ADR-004, ADR-012). One statement and a hand
/// written reader loop cost neither.
/// </para>
/// <para>
/// Nothing is materialised by reflection: columns are read by ordinal, and the three JSON columns go
/// through <see cref="DleDomainJsonContext"/>, the source generated metadata that SHARED-KERNEL §17.3
/// requires on this path.
/// </para>
/// <para>
/// Caching is not this type's job. The store is the miss path behind <c>HybridCache</c>, which supplies
/// the stampede protection: on a cache miss exactly one caller reaches this query and the rest wait for
/// its result (ADR-005, §C.3.1).
/// </para>
/// </remarks>
public sealed class DapperLinkStore : ILinkStore
{
    // Why the projection is wider than the covering index of §B.5.2:
    //
    // ix_links_resolve is (domain_id, slug) INCLUDE (target_url, deeplink_path, routing_rules,
    // og_meta, is_active, starts_at, expires_at, quarantined_at, tenant_id), and every column of
    // `links` below is either a key or an included column of it except utm, title, campaign_id and
    // expired_url. Those four are not optional: LinkSnapshot.Utm is required, and expired_url is what
    // separates the 302-to-a-farewell-page of TC-104 from a bare 404. Widening the INCLUDE list in the
    // migration is the cheap fix and the reason this comment names them.
    //
    // The joins are the honest part of the cost. Both consent modes are required to build the snapshot
    // and neither lives on `links`: the tenant's mode is on `tenants` and the domain override on
    // `domains`, and the gate needs both separately, since a domain override may only narrow what the
    // tenant permits (SHARED-KERNEL §2). The two lateral joins supply the per-domain application
    // fallbacks — store URL and custom scheme — that RoutingUrlBuilder falls back to when the matched
    // rule carries none. All of it is still one statement and one round trip, which is what the latency
    // budget actually cares about; the result is then cached for L1Seconds/L2Minutes, so the joins are
    // paid on a miss, never on a hit.
    //
    // The ::citext casts are load bearing. citext declares text -> citext as an assignment cast and
    // citext -> text as an implicit one, so `host = @host` with a text parameter resolves to text = text:
    // case sensitive, and unable to use the citext index. Casting the parameter instead keeps the
    // comparison citext = citext.
    private const string Sql = """
        SELECT l.id,
               l.tenant_id,
               l.domain_id,
               l.slug,
               l.target_url,
               l.deeplink_path,
               l.routing_rules,
               l.og_meta,
               l.utm,
               l.title,
               l.campaign_id,
               l.is_active,
               l.starts_at,
               l.expires_at,
               l.expired_url,
               l.quarantined_at,
               t.consent_mode                AS tenant_consent_mode,
               d.consent_mode_override       AS domain_consent_mode,
               ios.custom_scheme             AS ios_custom_scheme,
               ios.store_url                 AS ios_store_url,
               android.custom_scheme         AS android_custom_scheme,
               android.store_url             AS android_store_url
        FROM links l
        JOIN domains d ON d.id = l.domain_id
        JOIN tenants t ON t.id = l.tenant_id
        LEFT JOIN LATERAL (
            SELECT a.custom_scheme, a.store_url
            FROM apps a
            JOIN app_domains ad ON ad.app_id = a.id
            WHERE ad.domain_id = l.domain_id AND lower(a.platform) = 'ios'
            ORDER BY a.created_at
            LIMIT 1
        ) ios ON true
        LEFT JOIN LATERAL (
            SELECT a.custom_scheme, a.store_url
            FROM apps a
            JOIN app_domains ad ON ad.app_id = a.id
            WHERE ad.domain_id = l.domain_id AND lower(a.platform) = 'android'
            ORDER BY a.created_at
            LIMIT 1
        ) android ON true
        WHERE d.host = @host::citext
          AND l.slug = @slug::citext
          AND d.is_active
        """;

    private readonly DleReadDataSource _readDataSource;
    private readonly int _commandTimeoutSeconds;

    /// <summary>
    /// Creates the store.
    /// </summary>
    /// <param name="readDataSource">The read pool, which is a replica under §B.8 profile B.</param>
    /// <param name="options">Hot-path persistence options; supplies the command timeout.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public DapperLinkStore(DleReadDataSource readDataSource, FastPersistenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(readDataSource);
        ArgumentNullException.ThrowIfNull(options);

        _readDataSource = readDataSource;
        _commandTimeoutSeconds = options.CommandTimeoutSeconds;
    }

    /// <inheritdoc />
    public async ValueTask<LinkSnapshot?> FindAsync(string host, string slug, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        await using NpgsqlCommand command = _readDataSource.DataSource.CreateCommand(Sql);
        command.CommandTimeout = _commandTimeoutSeconds;
        command.Parameters.Add(new NpgsqlParameter<string>("host", host));
        command.Parameters.Add(new NpgsqlParameter<string>("slug", slug));

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);

        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    /// <summary>
    /// Builds the snapshot from the current row. Ordinals mirror the projection above, in order.
    /// </summary>
    private static LinkSnapshot Map(DbDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        TenantId = reader.GetGuid(1),
        DomainId = reader.GetGuid(2),
        Slug = reader.GetString(3),
        TargetUrl = reader.GetString(4),
        DeeplinkPath = PostgresValues.String(reader, 5),
        RoutingRules = ReadRoutingRules(reader, 6),
        Og = ReadOgMeta(reader, 7),
        Utm = ReadUtm(reader, 8),
        Title = PostgresValues.String(reader, 9),
        CampaignId = PostgresValues.Uuid(reader, 10),
        IsActive = reader.GetBoolean(11),
        StartsAt = PostgresValues.Timestamp(reader, 12),
        ExpiresAt = PostgresValues.Timestamp(reader, 13),
        ExpiredUrl = PostgresValues.String(reader, 14),
        QuarantinedAt = PostgresValues.Timestamp(reader, 15),
        TenantConsentMode = PostgresValues.ParseConsentMode(PostgresValues.String(reader, 16), ConsentMode.Off),
        DomainConsentMode = ReadOptionalConsentMode(reader, 17),
        IosCustomScheme = PostgresValues.String(reader, 18),
        IosStoreUrl = PostgresValues.String(reader, 19),
        AndroidCustomScheme = PostgresValues.String(reader, 20),
        AndroidStoreUrl = PostgresValues.String(reader, 21),
    };

    /// <summary>
    /// Decodes the <c>routing_rules</c> JSON document.
    /// </summary>
    /// <remarks>
    /// A malformed document yields an empty rule set rather than an exception. The routing engine turns
    /// that into <c>DecisionKind.NotFound</c>, which is a 404 for one link; letting the exception escape
    /// would instead be a 500 for whatever share of traffic that link carries, and the rules were
    /// validated on write by <c>RoutingRuleValidator</c> anyway.
    /// </remarks>
    private static RoutingRule[] ReadRoutingRules(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return [];
        }

        RoutingRule[]? rules = JsonSerializer.Deserialize(reader.GetString(ordinal), DleDomainJsonContext.Default.RoutingRuleArray);

        return rules ?? [];
    }

    /// <summary>Decodes the <c>og_meta</c> JSON document, falling back to the format defaults.</summary>
    private static OgMeta ReadOgMeta(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return OgMeta.Empty;
        }

        OgMeta? meta = JsonSerializer.Deserialize(reader.GetString(ordinal), DleDomainJsonContext.Default.OgMeta);

        return meta is null ? OgMeta.Empty : meta.MergeWith(OgMeta.Empty);
    }

    /// <summary>Decodes the <c>utm</c> JSON document.</summary>
    private static IReadOnlyDictionary<string, string> ReadUtm(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return ReadOnlyDictionary<string, string>.Empty;
        }

        Dictionary<string, string>? utm =
            JsonSerializer.Deserialize(reader.GetString(ordinal), DleDomainJsonContext.Default.DictionaryStringString);

        return utm is null || utm.Count == 0 ? ReadOnlyDictionary<string, string>.Empty : utm;
    }

    /// <summary>Decodes the optional per-domain consent override.</summary>
    private static ConsentMode? ReadOptionalConsentMode(DbDataReader reader, int ordinal)
    {
        string? raw = PostgresValues.String(reader, ordinal);

        return raw is null ? null : PostgresValues.ParseConsentMode(raw, ConsentMode.Off);
    }
}
