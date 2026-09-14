-- Deep Link Engine — ClickHouse analytics schema (ADR-006, docs/zadanie.md §B.5.3).
--
-- This script is idempotent and is applied by ClickHouseSchemaScript.Build(rawRetentionDays),
-- which substitutes the {{RAW_RETENTION_DAYS}} placeholder with the configured raw retention
-- window so that the TTL clauses below always match Dle:Privacy:Retention:RawDays (FR-247).
--
-- Two supported ways to fill these tables (§B.5.3 / ADR-006):
--   1. ClickHouseEventSink — the engine writes the click stream here directly, either instead of
--      or in addition to PostgreSQL (dual write).
--   2. Logical replication from PostgreSQL — the click stream keeps landing in the partitioned
--      PostgreSQL table and a CDC pipeline mirrors it here. The mirror tables below use
--      ReplacingMergeTree with an explicit version column precisely so that a replication stream
--      can re-deliver a row without duplicating it.
--
-- click_events is the only table the engine writes on the hot path. installs, attributions,
-- sdk_events and links are needed by the funnel and attribution-quality reports; in a replication
-- deployment they arrive from the control plane rather than from the engine.

CREATE TABLE IF NOT EXISTS click_events
(
    id            UUID,
    occurred_at   DateTime64(3, 'UTC'),
    tenant_id     UUID,
    link_id       Int64,
    click_id      String,
    ip_hash       String,
    ip_prefix     String,
    ua_family     LowCardinality(String),
    os_family     LowCardinality(String),
    os_version    String,
    device_class  LowCardinality(String),
    country       LowCardinality(String),
    region        String,
    language      LowCardinality(String),
    referrer_host String,
    channel       LowCardinality(String),
    decision      LowCardinality(String),
    -- -1 encodes both "no A/B assignment" and "not measured". A Nullable column on the
    -- hot table would cost a second stream per value for no reporting benefit.
    ab_bucket     Int16 DEFAULT -1,
    consent_mode  LowCardinality(String),
    is_bot        UInt8,
    spoofed_bot   UInt8,
    latency_ms    Int16 DEFAULT -1,
    extra         Map(String, String),
    -- Derived once at insert time so that every report groups platforms identically, and so that
    -- the expression cannot drift between the two providers.
    platform      LowCardinality(String) MATERIALIZED multiIf(
                      lower(device_class) = 'bot', 'bot',
                      lower(os_family) IN ('ios', 'ipados', 'iphone os', 'watchos', 'tvos'), 'ios',
                      startsWith(lower(os_family), 'android'), 'android',
                      lower(os_family) IN ('windows', 'windows nt', 'mac os x', 'macos', 'linux',
                                           'ubuntu', 'fedora', 'chrome os', 'chromeos'), 'desktop',
                      os_family = '', 'unknown',
                      'other')
)
ENGINE = MergeTree
PARTITION BY toYYYYMM(occurred_at)
ORDER BY (tenant_id, link_id, occurred_at)
TTL toDateTime(occurred_at) + INTERVAL {{RAW_RETENTION_DAYS}} DAY
SETTINGS index_granularity = 8192;

CREATE TABLE IF NOT EXISTS sdk_events
(
    id          UUID,
    occurred_at DateTime64(3, 'UTC'),
    tenant_id   UUID,
    app_id      UUID,
    install_id  String,
    type        LowCardinality(String),
    name        String,
    url         String,
    value       Nullable(Decimal(18, 4)),
    currency    LowCardinality(String),
    link_id     Int64,
    click_id    String,
    properties  Map(String, String)
)
ENGINE = MergeTree
PARTITION BY toYYYYMM(occurred_at)
ORDER BY (tenant_id, app_id, occurred_at)
TTL toDateTime(occurred_at) + INTERVAL {{RAW_RETENTION_DAYS}} DAY
SETTINGS index_granularity = 8192;

CREATE TABLE IF NOT EXISTS installs
(
    id            UUID,
    tenant_id     UUID,
    app_id        UUID,
    install_id    String,
    first_open_at DateTime64(3, 'UTC'),
    platform      LowCardinality(String),
    app_version   String,
    row_version   UInt64 DEFAULT 0
)
ENGINE = ReplacingMergeTree(row_version)
PARTITION BY toYYYYMM(first_open_at)
ORDER BY (tenant_id, app_id, install_id);

CREATE TABLE IF NOT EXISTS attributions
(
    id             UUID,
    tenant_id      UUID,
    install_id     UUID,
    click_id       String,
    link_id        Int64,
    match_type     LowCardinality(String),
    confidence     Decimal(3, 2),
    matched_at     DateTime64(3, 'UTC'),
    window_seconds Int32,
    row_version    UInt64 DEFAULT 0
)
ENGINE = ReplacingMergeTree(row_version)
PARTITION BY toYYYYMM(matched_at)
ORDER BY (tenant_id, install_id);

-- Minimal mirror of the control-plane links table. Only the columns the reports group or filter
-- by are replicated; nothing here is user content, so the mirror carries no retention risk.
CREATE TABLE IF NOT EXISTS links
(
    id          Int64,
    tenant_id   UUID,
    campaign_id UUID,
    row_version UInt64 DEFAULT 0
)
ENGINE = ReplacingMergeTree(row_version)
ORDER BY (tenant_id, id);
