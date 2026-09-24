using System.Globalization;

using Npgsql;

using NpgsqlTypes;

namespace Dle.IntegrationTests.Infrastructure;

/// <summary>
/// Writes the rows a test needs, straight into the schema the migration produced.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately SQL rather than EF Core. Half of what these tests assert is about the physical
/// schema — a <c>citext</c> comparison, a <c>jsonb</c> round trip, a partial unique index, a
/// partition boundary — and going through the object model would put a translation layer between
/// the assertion and the thing being asserted. The repository tests use the repositories; everything
/// else uses this.
/// </para>
/// <para>
/// Identifiers are handed out by a counter rather than drawn at random. A link's identifier is a
/// Snowflake in production, minted by the application before the insert; here it only has to be
/// unique and reproducible, and a random one would make a failing run harder to read for no gain.
/// </para>
/// </remarks>
public static class TestSeed
{
    private static long _linkId = 7_000_000_000_000_000L;

    /// <summary>Mints the next link identifier.</summary>
    /// <returns>A unique, increasing identifier.</returns>
    public static long NextLinkId() => Interlocked.Increment(ref _linkId);

    /// <summary>Inserts a tenant.</summary>
    /// <param name="database">The test database.</param>
    /// <param name="slug">Tenant slug; unique per instance.</param>
    /// <param name="consentMode">Stored consent mode: <c>off</c>, <c>aggregate_only</c> or <c>full</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tenant identifier the database generated.</returns>
    public static async Task<Guid> TenantAsync(
        TestDatabase database,
        string slug,
        string consentMode = "aggregate_only",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);

        return await Sql.ScalarAsync<Guid>(
            database.DataSource,
            """
            INSERT INTO tenants (slug, name, status, consent_mode)
            VALUES ($1, $2, 'active', $3)
            RETURNING id
            """,
            [slug, slug, consentMode],
            cancellationToken);
    }

    /// <summary>Inserts a link domain.</summary>
    /// <param name="database">The test database.</param>
    /// <param name="tenantId">Owning tenant.</param>
    /// <param name="host">Host, stored as <c>citext</c>.</param>
    /// <param name="consentOverride">Per-domain consent narrowing, or null.</param>
    /// <param name="isActive">Whether the domain serves.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The domain identifier.</returns>
    public static async Task<Guid> DomainAsync(
        TestDatabase database,
        Guid tenantId,
        string host,
        string? consentOverride = null,
        bool isActive = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);

        return await Sql.ScalarAsync<Guid>(
            database.DataSource,
            """
            INSERT INTO domains (tenant_id, host, is_default, tls_status, aasa_status,
                                 assetlinks_status, consent_mode_override, is_active)
            VALUES ($1, $2, true, 'verified', 'verified', 'verified', $3, $4)
            RETURNING id
            """,
            [tenantId, host, consentOverride, isActive],
            cancellationToken);
    }

    /// <summary>Inserts a link.</summary>
    /// <param name="database">The test database.</param>
    /// <param name="tenantId">Owning tenant.</param>
    /// <param name="domainId">Domain the slug lives on.</param>
    /// <param name="slug">Slug, stored as <c>citext</c>.</param>
    /// <param name="targetUrl">Web fallback.</param>
    /// <param name="routingRulesJson">The <c>routing_rules</c> document.</param>
    /// <param name="ogJson">The <c>og_meta</c> document.</param>
    /// <param name="utmJson">The <c>utm</c> document.</param>
    /// <param name="isActive">Whether the link serves.</param>
    /// <param name="startsAt">Not servable before this instant.</param>
    /// <param name="expiresAt">Not servable from this instant.</param>
    /// <param name="expiredUrl">Where an expired link redirects (TC-104).</param>
    /// <param name="quarantinedAt">When the link was withdrawn for abuse (TC-103).</param>
    /// <param name="title">Link title.</param>
    /// <param name="linkId">Identifier to use; one is minted when omitted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The link identifier.</returns>
    public static async Task<long> LinkAsync(
        TestDatabase database,
        Guid tenantId,
        Guid domainId,
        string slug,
        string targetUrl,
        string? routingRulesJson = null,
        string ogJson = "{}",
        string utmJson = "{}",
        bool isActive = true,
        DateTimeOffset? startsAt = null,
        DateTimeOffset? expiresAt = null,
        string? expiredUrl = null,
        DateTimeOffset? quarantinedAt = null,
        string? title = null,
        long? linkId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);

        long id = linkId ?? NextLinkId();

        _ = await Sql.ExecuteAsync(
            database.DataSource,
            """
            INSERT INTO links (id, tenant_id, domain_id, slug, title, target_url, routing_rules,
                               og_meta, utm, is_active, starts_at, expires_at, expired_url,
                               quarantined_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb, $8::jsonb, $9::jsonb, $10, $11, $12, $13, $14)
            """,
            [
                id,
                tenantId,
                domainId,
                slug,
                title,
                targetUrl,
                routingRulesJson ?? TestRules.WebDefault(),
                ogJson,
                utmJson,
                isActive,
                startsAt,
                expiresAt,
                expiredUrl,
                quarantinedAt,
            ],
            cancellationToken);

        return id;
    }

    /// <summary>Inserts an application and associates it with a domain.</summary>
    /// <param name="database">The test database.</param>
    /// <param name="tenantId">Owning tenant.</param>
    /// <param name="domainId">Domain to associate, or null to leave it unassociated.</param>
    /// <param name="platform">Stored platform: <c>ios</c> or <c>android</c>.</param>
    /// <param name="bundleId">Bundle or package identifier.</param>
    /// <param name="teamId">Apple team identifier; required for a usable AASA entry.</param>
    /// <param name="storeUrl">Store URL used as the per-domain fallback.</param>
    /// <param name="customScheme">Custom URI scheme.</param>
    /// <param name="playSigningFingerprints">Play App Signing certificate fingerprints.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The application identifier.</returns>
    public static async Task<Guid> AppAsync(
        TestDatabase database,
        Guid tenantId,
        Guid? domainId,
        string platform,
        string bundleId,
        string? teamId = null,
        string? storeUrl = null,
        string? customScheme = null,
        string[]? playSigningFingerprints = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);

        Guid appId = await Sql.ScalarAsync<Guid>(
            database.DataSource,
            """
            INSERT INTO apps (tenant_id, platform, bundle_id, team_id, store_url, custom_scheme,
                              play_signing_fingerprints)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            RETURNING id
            """,
            [tenantId, platform, bundleId, teamId, storeUrl, customScheme, playSigningFingerprints ?? []],
            cancellationToken);

        if (domainId is Guid domain)
        {
            _ = await Sql.ExecuteAsync(
                database.DataSource,
                "INSERT INTO app_domains (app_id, domain_id) VALUES ($1, $2)",
                [appId, domain],
                cancellationToken);
        }

        return appId;
    }

    /// <summary>Inserts an installation.</summary>
    /// <param name="database">The test database.</param>
    /// <param name="tenantId">Owning tenant.</param>
    /// <param name="appId">Application the installation belongs to.</param>
    /// <param name="installId">SDK generated identifier, unique per application.</param>
    /// <param name="platform">Stored platform.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The installation's row identifier.</returns>
    public static async Task<Guid> InstallAsync(
        TestDatabase database,
        Guid tenantId,
        Guid appId,
        string installId,
        string platform = "android",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);

        return await Sql.ScalarAsync<Guid>(
            database.DataSource,
            """
            INSERT INTO installs (tenant_id, app_id, install_id, first_open_at, platform)
            VALUES ($1, $2, $3, now(), $4)
            RETURNING id
            """,
            [tenantId, appId, installId, platform],
            cancellationToken);
    }

    /// <summary>Inserts an attribution.</summary>
    /// <param name="database">The test database.</param>
    /// <param name="tenantId">Owning tenant.</param>
    /// <param name="installRowId">The <c>installs.id</c> value.</param>
    /// <param name="clickId">Click identifier, or null for a match that read no click.</param>
    /// <param name="linkId">Link the install was credited to.</param>
    /// <param name="matchType">Match type name.</param>
    /// <param name="confidence">Confidence, 0.00 to 1.00.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The attribution's row identifier.</returns>
    public static async Task<Guid> AttributionAsync(
        TestDatabase database,
        Guid tenantId,
        Guid installRowId,
        string? clickId,
        long? linkId,
        string matchType = "install_referrer",
        decimal confidence = 1.00m,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);

        return await Sql.ScalarAsync<Guid>(
            database.DataSource,
            """
            INSERT INTO attributions (tenant_id, install_id, click_id, link_id, match_type,
                                      confidence, matched_at)
            VALUES ($1, $2, $3, $4, $5, $6, now())
            RETURNING id
            """,
            [tenantId, installRowId, clickId, linkId, matchType, confidence],
            cancellationToken);
    }

    /// <summary>Inserts one click event into the partitioned click stream.</summary>
    /// <param name="database">The test database.</param>
    /// <param name="tenantId">Owning tenant.</param>
    /// <param name="linkId">Link the click was on.</param>
    /// <param name="clickId">Public click identifier.</param>
    /// <param name="occurredAt">When it happened; decides the partition.</param>
    /// <param name="decision">Decision name from <c>DecisionNames</c>.</param>
    /// <param name="isBot">Whether the client was a confirmed crawler.</param>
    /// <param name="ipPrefix">Network prefix, or null.</param>
    /// <param name="osFamily">Operating system family.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the row is written.</returns>
    public static async Task ClickEventAsync(
        TestDatabase database,
        Guid tenantId,
        long linkId,
        string clickId,
        DateTimeOffset occurredAt,
        string decision = "web",
        bool isBot = false,
        string? ipPrefix = null,
        string? osFamily = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);

        await using NpgsqlCommand command = database.DataSource.CreateCommand(
            """
            INSERT INTO click_events (occurred_at, tenant_id, link_id, click_id, decision,
                                      consent_mode, is_bot, ip_prefix, os_family)
            VALUES (@occurred_at, @tenant_id, @link_id, @click_id, @decision,
                    'full', @is_bot, @ip_prefix::inet, @os_family)
            """);

        command.Parameters.Add(new NpgsqlParameter<DateTime>("occurred_at", occurredAt.UtcDateTime)
        {
            NpgsqlDbType = NpgsqlDbType.TimestampTz,
        });
        command.Parameters.Add(new NpgsqlParameter<Guid>("tenant_id", tenantId));
        command.Parameters.Add(new NpgsqlParameter<long>("link_id", linkId));
        command.Parameters.Add(new NpgsqlParameter<string>("click_id", clickId));
        command.Parameters.Add(new NpgsqlParameter<string>("decision", decision));
        command.Parameters.Add(new NpgsqlParameter<bool>("is_bot", isBot));
        command.Parameters.Add(new NpgsqlParameter("ip_prefix", NpgsqlDbType.Text)
        {
            Value = ipPrefix is null ? DBNull.Value : ipPrefix,
        });
        command.Parameters.Add(new NpgsqlParameter("os_family", NpgsqlDbType.Text)
        {
            Value = osFamily is null ? DBNull.Value : osFamily,
        });

        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>A host name unique to one test, so that cache keys never collide between tests.</summary>
    /// <param name="discriminator">A short, readable name for the test.</param>
    /// <returns>A normalised host.</returns>
    /// <remarks>
    /// The link cache is keyed by host and slug and lives for the life of the process, so two tests
    /// that both used <c>example.test</c> would see each other's entries. The counter makes that
    /// impossible without anyone having to remember it.
    /// </remarks>
    public static string UniqueHost(string discriminator) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{discriminator}-{Interlocked.Increment(ref _hostCounter)}.dle.test");

    private static int _hostCounter;
}
