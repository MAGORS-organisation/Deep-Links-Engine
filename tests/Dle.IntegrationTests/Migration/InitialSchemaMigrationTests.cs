using System.Globalization;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Dle.IntegrationTests.Migration;

/// <summary>
/// The first time <c>InitialSchema</c> meets a live PostgreSQL server (§B.5.2, §B.5.3, §D.7.5).
/// </summary>
/// <remarks>
/// Most of this migration is scaffolded and would fail loudly if it were wrong. Four parts are not:
/// the <c>dle_uuidv7()</c> shim, the declaratively partitioned <c>click_events</c> table, the
/// pg_partman handover with its fallback, and the per-table autovacuum setting on <c>links</c>.
/// Those are hand written SQL that no compiler checks, and every one of them fails silently rather
/// than loudly — a partitioned table with no partitions rejects inserts, an unset autovacuum
/// threshold quietly costs the index-only scan the latency budget of §B.6.1 depends on.
/// </remarks>
public sealed class InitialSchemaMigrationTests(DleInfrastructureFixture infrastructure)
    : DleIntegrationTest(infrastructure)
{
    /// <summary>
    /// Every table the product reads or writes, and therefore every table the migration owes.
    /// </summary>
    /// <remarks>
    /// <c>sdk_events</c> is on the list because <c>SdkEventBatchWriter</c> copies into it on the
    /// <c>POST /v1/events</c> path and <c>PostgresClickAnalyticsStore</c> reads it for the funnel and
    /// the time series (§B.6.4, FR-223, FR-202). A table a shipped code path writes to has to exist
    /// after the schema migration; there is nowhere else for it to come from.
    /// </remarks>
    private static readonly string[] RequiredTables =
    [
        "tenants",
        "domains",
        "apps",
        "app_domains",
        "campaigns",
        "links",
        "link_versions",
        "api_keys",
        "sdk_keys",
        "webhook_subscriptions",
        "webhook_deliveries",
        "installs",
        "attributions",
        "claim_codes",
        "abuse_reports",
        "audit_log",
        "signing_keys",
        "domain_verifications",
        "slug_sequences",
        "idempotency_records",
        "click_events",
        "sdk_events",
    ];

    /// <summary>The columns §B.5.2 requires <c>ix_links_resolve</c> to carry as included columns.</summary>
    private static readonly string[] ResolveIndexIncludedColumns =
    [
        "target_url",
        "deeplink_path",
        "routing_rules",
        "og_meta",
        "is_active",
        "starts_at",
        "expires_at",
        "quarantined_at",
        "tenant_id",
    ];

    [RequiresDockerFact]
    [Trait("Spec", "D.7.5")]
    public async Task Migration_AppliedToAnEmptyServer_CreatesEveryTableTheProductWritesTo()
    {
        List<string> missing = [];

        foreach (string table in RequiredTables)
        {
            if (!await Sql.RelationExistsAsync(Database.DataSource, table, Ct))
            {
                missing.Add(table);
            }
        }

        Assert.True(
            missing.Count == 0,
            "The InitialSchema migration did not create every table the product reads and writes. "
            + "Missing: " + string.Join(", ", missing) + ". A table a shipped code path writes to "
            + "has to exist after the schema migration.");
    }

    [RequiresDockerFact]
    [Trait("Spec", "B.5.2")]
    public async Task Migration_CreatesCitextExtension_AndUsesItForSlugAndHost()
    {
        Assert.True(
            await Sql.ScalarAsync<bool>(
                Database.DataSource,
                "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'citext')",
                cancellationToken: Ct),
            "citext is what makes slug and host comparisons case insensitive without a functional index.");

        Assert.Equal("citext", await ColumnTypeAsync("links", "slug"));
        Assert.Equal("citext", await ColumnTypeAsync("domains", "host"));
        Assert.Equal("citext", await ColumnTypeAsync("tenants", "slug"));
    }

    [RequiresDockerFact]
    [Trait("Spec", "B.5.2")]
    public async Task Migration_DleUuidV7_ReturnsVersionSevenIdentifiers()
    {
        // Character 15 of the canonical text form is the version nibble: 8-4-4-4-12 puts it at the
        // start of the third group.
        IReadOnlyList<string> versions = await Sql.StringsAsync(
            Database.DataSource,
            "SELECT substring(dle_uuidv7()::text from 15 for 1) FROM generate_series(1, 32)",
            cancellationToken: Ct);

        Assert.Equal(32, versions.Count);
        Assert.All(versions, version => Assert.Equal("7", version));
    }

    [RequiresDockerFact]
    [Trait("Spec", "B.5.2")]
    public async Task Migration_DleUuidV7_ProducesTimeOrderedIdentifiers()
    {
        // The whole reason for the shim: a time ordered key turns random B-tree page splits into
        // appends and makes the BRIN index on the event tables usable at all.
        IReadOnlyList<string> generated = await Sql.StringsAsync(
            Database.DataSource,
            """
            SELECT id::text
            FROM (
                SELECT dle_uuidv7() AS id, pg_sleep(0.01)
                FROM generate_series(1, 8)
            ) s
            """,
            cancellationToken: Ct);

        Assert.Equal(8, generated.Count);

        // The first twelve hex characters are the Unix millisecond timestamp, most significant
        // first, so lexicographic order over them is chronological order.
        List<string> timestamps = [.. generated.Select(id => id.Replace("-", string.Empty, StringComparison.Ordinal)[..12])];

        Assert.Equal(timestamps.Order(StringComparer.Ordinal), timestamps);
    }

    [RequiresDockerFact]
    [Trait("Spec", "B.5.3")]
    public async Task Migration_ClickEvents_IsRangePartitionedByOccurredAt()
    {
        string? strategy = await Sql.ScalarAsync<string>(
            Database.DataSource,
            """
            SELECT pt.partstrat::text
            FROM pg_partitioned_table pt
            JOIN pg_class c ON c.oid = pt.partrelid
            WHERE c.relname = 'click_events'
            """,
            cancellationToken: Ct);

        Assert.Equal("r", strategy);

        string? key = await Sql.ScalarAsync<string>(
            Database.DataSource,
            "SELECT pg_get_partkeydef('click_events'::regclass)",
            cancellationToken: Ct);

        Assert.Equal("RANGE (occurred_at)", key);
    }

    [RequiresDockerFact]
    [Trait("Spec", "B.5.3")]
    public async Task Migration_ClickEvents_HasPartitionsSoThatAnInsertSucceeds()
    {
        // A partitioned table with no partitions rejects every insert. pg_partman is not in a stock
        // image, so the migration's fallback maintenance function has to have run — this asserts it
        // did, by the only means that matters.
        long partitions = await Sql.ScalarAsync<long>(
            Database.DataSource,
            """
            SELECT count(*)
            FROM pg_inherits
            JOIN pg_class parent ON parent.oid = pg_inherits.inhparent
            WHERE parent.relname = 'click_events'
            """,
            cancellationToken: Ct);

        Assert.True(
            partitions > 0,
            "click_events has no partitions, so every click event insert would fail. The migration's "
            + "pg_partman handover is supposed to fall back to dle_click_events_maintain(7, 180).");

        Guid tenantId = await TestSeed.TenantAsync(Database, "partition-probe", cancellationToken: Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, TestSeed.UniqueHost("partition"), cancellationToken: Ct);
        long linkId = await TestSeed.LinkAsync(Database, tenantId, domainId, "probe", "https://example.test/", cancellationToken: Ct);

        // The server's clock, not the test process's: the partitions were created by the migration
        // against the server's idea of today, and on a machine whose clock differs by a day the
        // insert would land in the default partition and prove nothing.
        DateTime serverNow = await Sql.ScalarAsync<DateTime>(
            Database.DataSource,
            "SELECT (now() AT TIME ZONE 'UTC')",
            cancellationToken: Ct);

        await TestSeed.ClickEventAsync(
            Database,
            tenantId,
            linkId,
            "probe-click",
            new DateTimeOffset(serverNow, TimeSpan.Zero),
            cancellationToken: Ct);

        Assert.Equal(
            1L,
            await Sql.ScalarAsync<long>(
                Database.DataSource,
                "SELECT count(*) FROM click_events WHERE click_id = $1",
                ["probe-click"],
                Ct));
    }

    [RequiresDockerFact]
    [Trait("Spec", "B.5.3")]
    public async Task Migration_ClickEvents_HasTheBrinAndBtreeIndexesOfB53()
    {
        IReadOnlyList<string> definitions = await Sql.StringsAsync(
            Database.DataSource,
            """
            SELECT indexdef
            FROM pg_indexes
            WHERE schemaname = 'public' AND tablename = 'click_events'
            ORDER BY indexname
            """,
            cancellationToken: Ct);

        string all = string.Join("\n", definitions);

        Assert.Contains("USING brin (occurred_at)", all, StringComparison.Ordinal);
        Assert.Contains("ix_click_events_clickid", all, StringComparison.Ordinal);
        Assert.Contains("ix_click_events_link", all, StringComparison.Ordinal);
    }

    [RequiresDockerFact]
    [Trait("Spec", "B.5.2")]
    public async Task Migration_LinksAutovacuum_IsTunedSoTheVisibilityMapStaysFresh()
    {
        IReadOnlyList<string> options = await Sql.StringsAsync(
            Database.DataSource,
            """
            SELECT unnest(reloptions)
            FROM pg_class
            WHERE relname = 'links' AND relkind = 'r'
            """,
            cancellationToken: Ct);

        Assert.Contains("autovacuum_vacuum_scale_factor=0.02", options, StringComparer.Ordinal);
    }

    [RequiresDockerFact]
    [Trait("Spec", "B.5.2")]
    public async Task Migration_LinksResolveIndex_IncludesEveryColumnB52Names()
    {
        string? definition = await Sql.ScalarAsync<string>(
            Database.DataSource,
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'ix_links_resolve'",
            cancellationToken: Ct);

        Assert.NotNull(definition);
        Assert.Contains("(domain_id, slug)", definition, StringComparison.Ordinal);

        foreach (string column in ResolveIndexIncludedColumns)
        {
            Assert.Contains(column, definition, StringComparison.Ordinal);
        }
    }

    [RequiresDockerFact]
    [Trait("Spec", "B.5.2")]
    public async Task Migration_UniqueIndexes_AreCreatedWithTheFiltersB53Requires()
    {
        string? attributionsByClick = await Sql.ScalarAsync<string>(
            Database.DataSource,
            "SELECT indexdef FROM pg_indexes WHERE indexname = 'uq_attributions_click'",
            cancellationToken: Ct);

        Assert.NotNull(attributionsByClick);
        Assert.Contains("UNIQUE", attributionsByClick, StringComparison.Ordinal);
        Assert.Contains("WHERE (click_id IS NOT NULL)", attributionsByClick, StringComparison.Ordinal);

        string? attributionsByInstall = await Sql.ScalarAsync<string>(
            Database.DataSource,
            "SELECT indexdef FROM pg_indexes WHERE indexname = 'uq_attributions_install'",
            cancellationToken: Ct);

        Assert.NotNull(attributionsByInstall);
        Assert.Contains("UNIQUE", attributionsByInstall, StringComparison.Ordinal);
    }

    [RequiresDockerFact]
    [Trait("Spec", "D.7.5")]
    public async Task Migration_RolledBack_RemovesEveryObjectItCreated()
    {
        // Release criterion 5 of §D.7: migrations are tested including the rollback. A Down that
        // leaves the partitioned table or the identifier function behind makes the next Up fail on
        // a database an operator has already rolled back once.
        await using ControlPlaneScope scope = ControlPlaneScope.Open(Database);

        IMigrator migrator = scope.Db.GetInfrastructure().GetRequiredService<IMigrator>();

        await migrator.MigrateAsync(Microsoft.EntityFrameworkCore.Migrations.Migration.InitialDatabase, Ct);

        foreach (string table in RequiredTables)
        {
            if (string.Equals(table, "sdk_events", StringComparison.Ordinal))
            {
                // Nothing creates it, so nothing can drop it. Covered by the completeness test above.
                continue;
            }

            Assert.False(
                await Sql.RelationExistsAsync(Database.DataSource, table, Ct),
                string.Create(CultureInfo.InvariantCulture, $"Table {table} survived the rollback."));
        }

        Assert.False(
            await Sql.ScalarAsync<bool>(
                Database.DataSource,
                "SELECT EXISTS (SELECT 1 FROM pg_proc WHERE proname = 'dle_uuidv7')",
                cancellationToken: Ct),
            "dle_uuidv7() survived the rollback, so a second Up would fail on CREATE OR REPLACE of a "
            + "function whose dependent defaults are gone.");

        Assert.False(
            await Sql.ScalarAsync<bool>(
                Database.DataSource,
                "SELECT EXISTS (SELECT 1 FROM pg_proc WHERE proname = 'dle_click_events_maintain')",
                cancellationToken: Ct),
            "dle_click_events_maintain() survived the rollback.");
    }

    /// <summary>Reads the PostgreSQL type of one column.</summary>
    private async Task<string?> ColumnTypeAsync(string table, string column) =>
        await Sql.ScalarAsync<string>(
            Database.DataSource,
            """
            SELECT format_type(a.atttypid, a.atttypmod)
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relname = $1 AND a.attname = $2
            """,
            [table, column],
            Ct);
}
