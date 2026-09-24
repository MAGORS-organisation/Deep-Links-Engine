-- Deep Link Engine — PostgreSQL analytics rollup schema.
-- ADR-006 (Postgres partitions first), ADR-008 (honest attribution dashboard),
-- FR-202 (aggregations), FR-247 / §E.6.3 (retention and its audit trail).
--
-- Idempotent. This is the script the persistence migration applies; nothing in this file depends
-- on the analytics module running, and nothing in the analytics module creates schema at startup.
--
-- Grain and dimensions. The click rollups aggregate the partitioned click_events table by link,
-- campaign, country, platform and channel, hourly and daily. Bots are rolled up too, under
-- is_bot = true, rather than being dropped: FR-205 says crawler traffic must not be counted in a
-- campaign, not that it must be unobservable. The reporting store filters on is_bot, so
-- AnalyticsQuery.IncludeBots keeps working against the rollups exactly as it does against raw
-- events (TC-106).
--
-- Why 'unknown' instead of NULL in the key columns. These columns are part of a primary key, and
-- a NULL in a key column would let the same logical bucket be inserted twice. The reporting store
-- reports the same literal for a missing dimension, so the rows always sum to the totals.

-- ---------------------------------------------------------------------------------------------
-- Platform derivation.
--
-- click_events stores os_family and device_class, not a platform. Both the rollup jobs and the
-- raw-event queries need one platform expression, and it must be the same expression, or a
-- breakdown served from the rollup would disagree with the same breakdown served from raw events.
-- Keeping it in one IMMUTABLE function is what makes that guarantee mechanical. The ClickHouse
-- provider applies the identical rule in a MATERIALIZED column.
-- ---------------------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION dle_platform_of(p_os_family text, p_device_class text)
RETURNS text
LANGUAGE sql
IMMUTABLE
PARALLEL SAFE
AS $dle_platform_of$
    -- Deliberately not STRICT: os_family and device_class are both nullable on click_events, and
    -- a NULL platform would poison a primary-key column in every rollup.
    SELECT CASE
        WHEN lower(coalesce(p_device_class, '')) = 'bot' THEN 'bot'
        WHEN lower(coalesce(p_os_family, '')) IN ('ios', 'ipados', 'iphone os', 'watchos', 'tvos')
            THEN 'ios'
        WHEN lower(coalesce(p_os_family, '')) LIKE 'android%' THEN 'android'
        WHEN lower(coalesce(p_os_family, '')) IN ('windows', 'windows nt', 'mac os x', 'macos',
                                                  'linux', 'ubuntu', 'fedora', 'chrome os',
                                                  'chromeos') THEN 'desktop'
        WHEN coalesce(p_os_family, '') = '' THEN 'unknown'
        ELSE 'other'
    END
$dle_platform_of$;

COMMENT ON FUNCTION dle_platform_of(text, text) IS
    'Canonical platform of a click event, derived from os_family and device_class. Shared by the '
    'rollup jobs and the raw-event reporting queries so the two can never disagree.';

-- ---------------------------------------------------------------------------------------------
-- Click rollups.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS click_rollup_hourly (
    bucket      timestamptz NOT NULL,
    tenant_id   uuid        NOT NULL,
    link_id     bigint      NOT NULL,
    country     text        NOT NULL,
    platform    text        NOT NULL,
    channel     text        NOT NULL,
    is_bot      boolean     NOT NULL,
    campaign_id uuid,
    clicks      bigint      NOT NULL,
    updated_at  timestamptz NOT NULL,
    CONSTRAINT pk_click_rollup_hourly
        PRIMARY KEY (bucket, tenant_id, link_id, country, platform, channel, is_bot)
);

CREATE INDEX IF NOT EXISTS ix_click_rollup_hourly_tenant
    ON click_rollup_hourly (tenant_id, bucket);

CREATE INDEX IF NOT EXISTS ix_click_rollup_hourly_campaign
    ON click_rollup_hourly (tenant_id, campaign_id, bucket)
    WHERE campaign_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS click_rollup_daily (
    bucket      timestamptz NOT NULL,
    tenant_id   uuid        NOT NULL,
    link_id     bigint      NOT NULL,
    country     text        NOT NULL,
    platform    text        NOT NULL,
    channel     text        NOT NULL,
    is_bot      boolean     NOT NULL,
    campaign_id uuid,
    clicks      bigint      NOT NULL,
    updated_at  timestamptz NOT NULL,
    CONSTRAINT pk_click_rollup_daily
        PRIMARY KEY (bucket, tenant_id, link_id, country, platform, channel, is_bot)
);

CREATE INDEX IF NOT EXISTS ix_click_rollup_daily_tenant
    ON click_rollup_daily (tenant_id, bucket);

CREATE INDEX IF NOT EXISTS ix_click_rollup_daily_campaign
    ON click_rollup_daily (tenant_id, campaign_id, bucket)
    WHERE campaign_id IS NOT NULL;

-- ---------------------------------------------------------------------------------------------
-- Install and conversion rollups.
--
-- link_id = 0 means "this install was never tied to a link". It is stored rather than dropped
-- because the gap between installs and attributed installs is a reported number (FunnelSummary),
-- not an accident.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS install_rollup_hourly (
    bucket           timestamptz NOT NULL,
    tenant_id        uuid        NOT NULL,
    link_id          bigint      NOT NULL,
    platform         text        NOT NULL,
    campaign_id      uuid,
    installs         bigint      NOT NULL,
    attributed       bigint      NOT NULL,
    conversions      bigint      NOT NULL,
    conversion_value numeric(18, 4),
    updated_at       timestamptz NOT NULL,
    CONSTRAINT pk_install_rollup_hourly PRIMARY KEY (bucket, tenant_id, link_id, platform)
);

CREATE INDEX IF NOT EXISTS ix_install_rollup_hourly_tenant
    ON install_rollup_hourly (tenant_id, bucket);

CREATE TABLE IF NOT EXISTS install_rollup_daily (
    bucket           timestamptz NOT NULL,
    tenant_id        uuid        NOT NULL,
    link_id          bigint      NOT NULL,
    platform         text        NOT NULL,
    campaign_id      uuid,
    installs         bigint      NOT NULL,
    attributed       bigint      NOT NULL,
    conversions      bigint      NOT NULL,
    conversion_value numeric(18, 4),
    updated_at       timestamptz NOT NULL,
    CONSTRAINT pk_install_rollup_daily PRIMARY KEY (bucket, tenant_id, link_id, platform)
);

CREATE INDEX IF NOT EXISTS ix_install_rollup_daily_tenant
    ON install_rollup_daily (tenant_id, bucket);

-- ---------------------------------------------------------------------------------------------
-- Attribution quality rollup — the data behind the ADR-008 dashboard panel.
--
-- One row per (day, tenant, link, match_type), carrying the number of matches and the sum of
-- their confidence. The deterministic / probabilistic / unmatched split is a partition of
-- match_type: install_referrer, login, claim_code and direct_open are deterministic, probabilistic
-- is probabilistic, and none is unmatched. The sum is stored rather than the mean so that rolling
-- several days together stays exact.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS attribution_quality_daily (
    bucket         timestamptz   NOT NULL,
    tenant_id      uuid          NOT NULL,
    link_id        bigint        NOT NULL,
    match_type     text          NOT NULL,
    matches        bigint        NOT NULL,
    confidence_sum numeric(14, 2) NOT NULL,
    updated_at     timestamptz   NOT NULL,
    CONSTRAINT pk_attribution_quality_daily
        PRIMARY KEY (bucket, tenant_id, link_id, match_type)
);

CREATE INDEX IF NOT EXISTS ix_attribution_quality_daily_tenant
    ON attribution_quality_daily (tenant_id, bucket);

-- ---------------------------------------------------------------------------------------------
-- Rollup watermark.
--
-- The reporting store reads a rollup only when the whole requested range is at or before the
-- watermark; otherwise it reads raw events. Without this row a report could silently serve a
-- half-filled bucket as though it were complete.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS analytics_rollup_state (
    name            text        NOT NULL PRIMARY KEY,
    covered_from    timestamptz,
    covered_through timestamptz NOT NULL,
    updated_at      timestamptz NOT NULL
);

-- covered_from arrived after covered_through and is nullable so that an instance created before it
-- keeps working. A NULL lower bound means "we do not know how far back this rollup reaches", and
-- the reporting store reads that as a reason to answer from the raw events instead: an unknown
-- lower bound must never be mistaken for a complete one.
ALTER TABLE analytics_rollup_state ADD COLUMN IF NOT EXISTS covered_from timestamptz;

-- ---------------------------------------------------------------------------------------------
-- Retention audit (FR-247, §E.6.3: "configurable retention with an automatic job and an audit of
-- its runs"). A retention job that cannot prove what it deleted and when is not auditable, so the
-- run is recorded even when it deleted nothing and even when it failed.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS analytics_retention_runs (
    id                  uuid        NOT NULL PRIMARY KEY,
    started_at          timestamptz NOT NULL,
    finished_at         timestamptz NOT NULL,
    raw_days            integer     NOT NULL,
    aggregated_days     integer     NOT NULL,
    ip_prefix_days      integer,
    dry_run             boolean     NOT NULL,
    partitions_dropped  text[]      NOT NULL DEFAULT '{}',
    rows_anonymised     bigint      NOT NULL DEFAULT 0,
    rollup_rows_deleted bigint      NOT NULL DEFAULT 0,
    status              text        NOT NULL,
    error               text
);

CREATE INDEX IF NOT EXISTS ix_analytics_retention_runs_started
    ON analytics_retention_runs (started_at DESC);
