namespace Dle.Persistence.Sql;

/// <summary>
/// The parts of the schema EF Core cannot express, kept as SQL and executed from the migration.
/// </summary>
/// <remarks>
/// <para>
/// Declarative partitioning, BRIN indexes, per-table autovacuum settings and a version-dependent
/// identifier default have no model level equivalent, and inventing one would only obscure what
/// actually reaches the database. They live here instead, next to the migration that runs them,
/// where they can be read as SQL and diffed as SQL.
/// </para>
/// <para>
/// Every script is idempotent: running the migration twice, or running it against a database an
/// operator has already partly prepared by hand, must not fail.
/// </para>
/// </remarks>
internal static class DlePostgresScripts
{
    /// <summary>
    /// Creates <c>dle_uuidv7()</c>, the identifier default for every uuid primary key.
    /// </summary>
    /// <remarks>
    /// PostgreSQL 18 has a native <c>uuidv7()</c>; the project floor is 16, which does not (ADR-003).
    /// The function therefore dispatches on the server version at call time and falls back to an
    /// RFC 9562 conformant construction: the top 48 bits are the Unix millisecond timestamp, the
    /// version nibble is forced to 7, and everything else stays the randomness
    /// <c>gen_random_uuid()</c> already produced — including the variant bits, which version 4 and
    /// version 7 share. Time ordered keys are the point: they turn random B-tree page splits into
    /// appends and make BRIN indexes usable on the event tables.
    /// </remarks>
    internal const string CreateUuidV7Function = """
        CREATE OR REPLACE FUNCTION dle_uuidv7() RETURNS uuid
        LANGUAGE plpgsql
        VOLATILE
        AS $dle_fn$
        DECLARE
            native uuid;
        BEGIN
            IF current_setting('server_version_num')::int >= 180000 THEN
                EXECUTE 'SELECT uuidv7()' INTO native;
                RETURN native;
            END IF;

            -- PostgreSQL 16/17 fallback. set_bit numbers bits from the least significant end of
            -- each byte, so the version nibble of byte 6 is bits 52..55: turning bits 52 and 53 on
            -- rewrites version 4 (0100) into version 7 (0111).
            RETURN encode(
                set_bit(
                    set_bit(
                        overlay(
                            uuid_send(gen_random_uuid())
                            PLACING substring(
                                int8send(floor(extract(epoch FROM clock_timestamp()) * 1000)::bigint)
                                FROM 3)
                            FROM 1 FOR 6),
                        52, 1),
                    53, 1),
                'hex')::uuid;
        END;
        $dle_fn$;

        COMMENT ON FUNCTION dle_uuidv7() IS
            'Time ordered UUID. Native uuidv7() on PostgreSQL 18+, RFC 9562 fallback on 16 and 17.';
        """;

    /// <summary>Drops <c>dle_uuidv7()</c>.</summary>
    internal const string DropUuidV7Function = """
        DROP FUNCTION IF EXISTS dle_uuidv7();
        """;

    /// <summary>
    /// Creates the partitioned click stream table of §B.5.3 with its three indexes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The primary key is <c>(occurred_at, id)</c> because a partitioned table has to include the
    /// partition key in every unique constraint. That is also why <c>attributions.click_id</c>
    /// cannot be a foreign key into this table: there is no unique constraint on <c>click_id</c>
    /// alone to reference, so the one-click-one-install invariant is enforced by a partial unique
    /// index on the attribution side instead (TC-144).
    /// </para>
    /// <para>
    /// The index choices follow the access patterns. <c>occurred_at</c> gets a BRIN index rather
    /// than a B-tree: the table is append-only and physically ordered by time, so a summary of a
    /// 128 page range per entry answers range scans at a fraction of the size. <c>click_id</c> gets
    /// a B-tree because attribution looks up one exact value. <c>(link_id, occurred_at DESC)</c>
    /// serves "the recent clicks of this link", which is the report query.
    /// </para>
    /// <para>
    /// <c>spoofed_bot</c> is not in the DDL sketch but is in the <c>ClickEvent</c> contract of the
    /// shared kernel: a user agent that claims to be a crawler while reverse DNS disagrees is
    /// recorded rather than silently trusted (TC-107).
    /// </para>
    /// </remarks>
    internal const string CreateClickEvents = """
        CREATE TABLE IF NOT EXISTS click_events (
            id             uuid        NOT NULL DEFAULT dle_uuidv7(),
            occurred_at    timestamptz NOT NULL,
            tenant_id      uuid        NOT NULL,
            link_id        bigint      NOT NULL,
            click_id       text        NOT NULL,
            ip_hash        bytea,
            ip_prefix      inet,
            ua_family      text,
            os_family      text,
            os_version     text,
            device_class   text,
            country        char(2),
            region         text,
            language       text,
            referrer_host  text,
            channel        text,
            decision       text        NOT NULL,
            ab_bucket      smallint,
            consent_mode   text        NOT NULL,
            is_bot         boolean     NOT NULL DEFAULT false,
            spoofed_bot    boolean     NOT NULL DEFAULT false,
            latency_ms     smallint,
            extra          jsonb       NOT NULL DEFAULT '{}',
            PRIMARY KEY (occurred_at, id)
        ) PARTITION BY RANGE (occurred_at);

        COMMENT ON TABLE click_events IS
            'Append only click stream, partitioned daily by occurred_at. Written in batches with
             COPY, never row by row. ip_hash is HMAC(ip, daily salt) and ip_prefix is only written
             under consent mode full.';

        CREATE INDEX IF NOT EXISTS ix_click_events_brin
            ON click_events USING brin (occurred_at);

        CREATE INDEX IF NOT EXISTS ix_click_events_clickid
            ON click_events (click_id);

        CREATE INDEX IF NOT EXISTS ix_click_events_link
            ON click_events (link_id, occurred_at DESC);
        """;

    /// <summary>Drops the click stream table and everything attached to it.</summary>
    internal const string DropClickEvents = """
        DROP TABLE IF EXISTS click_events CASCADE;
        """;

    /// <summary>
    /// Creates <c>dle_click_events_maintain()</c>, the partition maintenance used when pg_partman
    /// is unavailable.
    /// </summary>
    /// <remarks>
    /// pg_partman is optional: it is not in a stock PostgreSQL image and a managed instance may
    /// refuse to install it. A partitioned table with no partitions rejects every insert, so
    /// "degrade gracefully" cannot mean "do nothing" — it has to mean the click stream still
    /// works. This function pre-creates the days ahead and drops the days past retention, which is
    /// the whole of what pg_partman is asked to do here, and a cron entry or a hosted worker can
    /// call it on the same daily schedule.
    /// </remarks>
    internal const string CreateFallbackMaintenanceFunction = """
        CREATE OR REPLACE FUNCTION dle_click_events_maintain(
            p_days_ahead     int DEFAULT 7,
            p_retention_days int DEFAULT 180)
        RETURNS int
        LANGUAGE plpgsql
        AS $dle_fn$
        DECLARE
            today_utc      date := (now() AT TIME ZONE 'UTC')::date;
            partition_day  date;
            part_name      text;
            created        int := 0;
            victim         record;
        BEGIN
            IF to_regclass('public.click_events_default') IS NULL THEN
                CREATE TABLE public.click_events_default PARTITION OF public.click_events DEFAULT;
            END IF;

            FOR partition_day IN
                SELECT generate_series(
                           today_utc - 1,
                           today_utc + p_days_ahead,
                           interval '1 day')::date
            LOOP
                part_name := 'click_events_p' || to_char(partition_day, 'YYYYMMDD');

                IF to_regclass('public.' || quote_ident(part_name)) IS NULL THEN
                    EXECUTE format(
                        'CREATE TABLE public.%I PARTITION OF public.click_events '
                        'FOR VALUES FROM (%L) TO (%L)',
                        part_name,
                        (partition_day::timestamp AT TIME ZONE 'UTC'),
                        ((partition_day + 1)::timestamp AT TIME ZONE 'UTC'));
                    created := created + 1;
                END IF;
            END LOOP;

            FOR victim IN
                SELECT child.relname AS name
                  FROM pg_inherits
                  JOIN pg_class child  ON child.oid  = pg_inherits.inhrelid
                  JOIN pg_class parent ON parent.oid = pg_inherits.inhparent
                 WHERE parent.relname = 'click_events'
                   AND child.relname ~ '^click_events_p[0-9]{8}$'
                   AND to_date(right(child.relname, 8), 'YYYYMMDD')
                       < today_utc - p_retention_days
            LOOP
                EXECUTE format('DROP TABLE public.%I', victim.name);
            END LOOP;

            RETURN created;
        END;
        $dle_fn$;

        COMMENT ON FUNCTION dle_click_events_maintain(int, int) IS
            'Fallback daily partition maintenance for click_events when pg_partman is absent.
             Creates partitions p_days_ahead days ahead and drops those older than p_retention_days.';
        """;

    /// <summary>Drops the fallback maintenance function.</summary>
    internal const string DropFallbackMaintenanceFunction = """
        DROP FUNCTION IF EXISTS dle_click_events_maintain(int, int);
        """;

    /// <summary>
    /// Hands <c>click_events</c> to pg_partman for daily partitions, seven days pre-created and a
    /// retention of one hundred and eighty days — and says clearly what happened when pg_partman is
    /// not there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three things can go wrong and all three are handled rather than allowed to abort the
    /// migration: the extension may not be available in the image, the migration role may not be
    /// allowed to create an extension, and the installed pg_partman may be version 4 rather than
    /// version 5, whose <c>create_parent</c> signature differs. In every case the operator gets a
    /// warning naming the exact remedy, and the fallback maintenance function is invoked so the
    /// table is usable immediately.
    /// </para>
    /// <para>
    /// The retention is a delete, not an archive: <c>retention_keep_table = false</c> drops the
    /// partition. Aggregated data has a longer life than raw click rows, which is the retention
    /// split of §E.6.
    /// </para>
    /// </remarks>
    internal const string ConfigurePartitioning = """
        DO $dle_partman$
        DECLARE
            partman_schema text;
            handed_over    boolean := true;
        BEGIN
            SELECT nsp.nspname
              INTO partman_schema
              FROM pg_extension ext
              JOIN pg_namespace nsp ON nsp.oid = ext.extnamespace
             WHERE ext.extname = 'pg_partman';

            IF partman_schema IS NULL
               AND EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'pg_partman')
            THEN
                BEGIN
                    EXECUTE 'CREATE SCHEMA IF NOT EXISTS partman';
                    EXECUTE 'CREATE EXTENSION pg_partman WITH SCHEMA partman';
                    partman_schema := 'partman';
                EXCEPTION WHEN insufficient_privilege THEN
                    RAISE WARNING
                        'pg_partman is available but this role may not CREATE EXTENSION. Ask a '
                        'superuser to run: CREATE SCHEMA IF NOT EXISTS partman; CREATE EXTENSION '
                        'pg_partman WITH SCHEMA partman;';
                END;
            END IF;

            IF partman_schema IS NULL THEN
                RAISE WARNING
                    'pg_partman is not installed, so click_events partitions will be maintained by '
                    'dle_click_events_maintain(7, 180) instead. Schedule it daily (cron, '
                    'pg_cron or the DLE maintenance worker), or install pg_partman and re-run this '
                    'migration to hand the table over to it.';
                PERFORM dle_click_events_maintain(7, 180);
                RETURN;
            END IF;

            BEGIN
                -- pg_partman 5.x: native partitioning only, no p_type argument.
                EXECUTE format(
                    'SELECT %I.create_parent('
                    '  p_parent_table := %L,'
                    '  p_control      := %L,'
                    '  p_interval     := %L,'
                    '  p_premake      := 7)',
                    partman_schema, 'public.click_events', 'occurred_at', '1 day');
            EXCEPTION
                WHEN undefined_function OR invalid_parameter_value THEN
                    -- pg_partman 4.x: p_type is required and the interval is named.
                    EXECUTE format(
                        'SELECT %I.create_parent('
                        '  p_parent_table := %L,'
                        '  p_control      := %L,'
                        '  p_type         := %L,'
                        '  p_interval     := %L,'
                        '  p_premake      := 7)',
                        partman_schema, 'public.click_events', 'occurred_at', 'native', 'daily');
                WHEN unique_violation OR duplicate_table THEN
                    -- Already handed over by an earlier run of this migration.
                    NULL;
                WHEN OTHERS THEN
                    -- Anything else: say exactly what pg_partman refused and why this matters, then
                    -- fall back rather than abort the migration. Silence here would leave a
                    -- partitioned table with no partitions, which rejects every insert.
                    handed_over := false;
                    RAISE WARNING
                        'pg_partman refused to take over click_events (%: %). Partitions will be '
                        'maintained by dle_click_events_maintain(7, 180) instead; schedule it '
                        'daily. Fix the cause and re-run this migration to hand the table over.',
                        SQLSTATE, SQLERRM;
            END;

            IF NOT handed_over THEN
                PERFORM dle_click_events_maintain(7, 180);
                RETURN;
            END IF;

            EXECUTE format(
                'UPDATE %I.part_config'
                '   SET retention = %L,'
                '       retention_keep_table = false,'
                '       premake = 7'
                ' WHERE parent_table = %L',
                partman_schema, '180 days', 'public.click_events');
        END;
        $dle_partman$;
        """;

    /// <summary>Removes the pg_partman configuration for the click stream, if there is one.</summary>
    internal const string RemovePartitioning = """
        DO $dle_partman$
        DECLARE
            partman_schema text;
        BEGIN
            SELECT nsp.nspname
              INTO partman_schema
              FROM pg_extension ext
              JOIN pg_namespace nsp ON nsp.oid = ext.extnamespace
             WHERE ext.extname = 'pg_partman';

            IF partman_schema IS NULL THEN
                RETURN;
            END IF;

            EXECUTE format(
                'DELETE FROM %I.part_config WHERE parent_table = %L',
                partman_schema, 'public.click_events');
        END;
        $dle_partman$;
        """;

    /// <summary>
    /// Lowers the autovacuum threshold on <c>links</c> so the covering resolve index stays usable
    /// for index-only scans.
    /// </summary>
    /// <remarks>
    /// <c>INCLUDE</c> makes the index covering, but an index-only scan still consults the
    /// visibility map, and a stale map sends the planner back to the heap — at which point the
    /// latency budget of §B.6.1 no longer holds. Vacuuming after two per cent of the table has
    /// changed, rather than the default twenty, keeps the map fresh. Verify it with
    /// <c>EXPLAIN (ANALYZE, BUFFERS)</c>, do not assume it (planner note in §B.5.2).
    /// </remarks>
    internal const string TuneLinksAutovacuum = """
        ALTER TABLE links SET (autovacuum_vacuum_scale_factor = 0.02);
        """;

    /// <summary>Restores the default autovacuum threshold on <c>links</c>.</summary>
    internal const string ResetLinksAutovacuum = """
        ALTER TABLE links RESET (autovacuum_vacuum_scale_factor);
        """;
}
