-- ============================================================================
-- Deep Link Engine — extensions. Runs once, on first initialisation of an empty data directory,
-- as POSTGRES_USER (a superuser in the official image) connected to POSTGRES_DB.
--
-- citext: case-insensitive host names and slugs (links.slug, app_domains.host — see the
-- InitialSchema migration in src/Dle.Persistence/Migrations). The EF Core migration also declares
-- it (HasPostgresExtension), so this is belt and braces for a role that later loses CREATE.
-- ============================================================================
CREATE EXTENSION IF NOT EXISTS citext;

-- ----------------------------------------------------------------------------
-- pg_partman — daily click_events partitions and 180-day retention (docs/zadanie.md §B.5.2).
--
-- The OFFICIAL postgres image does NOT contain pg_partman, so this block is guarded: it installs
-- the extension when the server has it available and only warns when it has not. Options:
--   * deploy/docker/Dockerfile.postgres  — postgres:18 plus the PGDG postgresql-18-partman
--     package (POSTGRES_IMAGE=dle-postgres:18 in .env);
--   * an image that ships it (ghcr.io/dbsystel/postgresql-partman, CrunchyData) or a managed
--     service that offers it (RDS, Aurora, Azure Flexible Server, Cloud SQL);
--   * nothing — the InitialSchema migration (DlePostgresScripts.ConfigurePartitioning) detects
--     the absence at migration time, raises a WARNING, and installs dle_click_events_maintain()
--     and dle_sdk_events_maintain() — plain-SQL functions that pre-create 7 days of partitions and
--     drop those older than the retention — to be called daily (pg_cron, a cron job, or the DLE
--     maintenance worker).
--
-- Schema `partman` is what the migration looks for (it reads pg_extension for the installed
-- schema, so any schema works, but `partman` is the convention it creates itself).
-- ----------------------------------------------------------------------------
DO $dle_init$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'pg_partman') THEN
        EXECUTE 'CREATE SCHEMA IF NOT EXISTS partman';
        EXECUTE 'CREATE EXTENSION IF NOT EXISTS pg_partman WITH SCHEMA partman';
        RAISE NOTICE 'dle: pg_partman installed into schema partman';
    ELSE
        RAISE WARNING 'dle: pg_partman is not available in this PostgreSQL image; click_events and '
                      'sdk_events partitions will be maintained by the fallback functions the '
                      'migration installs (dle_click_events_maintain, dle_sdk_events_maintain). '
                      'See deploy/postgres/init/01-extensions.sql.';
    END IF;
END
$dle_init$;
