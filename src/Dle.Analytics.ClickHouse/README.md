# Dle.Analytics.ClickHouse (Experimental / v0.2 Target)

`Dle.Analytics.ClickHouse` provides an **experimental, query-only** `IClickAnalyticsStore` implementation intended for deployments where click analytics outgrow partitioned PostgreSQL tables.

## Current Scope (`v0.1` vs `v0.2`)

- **Query-only adapter:** Reads pre-aggregated and raw analytics from ClickHouse (`ReplacingMergeTree` / rollups) when `Dle:Analytics:Provider` is set to `clickhouse`.
- **No direct Edge ingestion yet (`v0.2` target):** `Dle.Edge` continues to write `click_events` directly to PostgreSQL in `v0.1`. Production use of this provider requires an external **PostgreSQL -> ClickHouse CDC** replication pipeline (e.g., PeerDB / Debezium / logical replication consumer) feeding the ClickHouse schema.
- **Keep default `postgres` provider for `v0.1`:** Leave `Dle:Analytics:Provider` on the default `postgres` setting unless you operate an external CDC pipeline into ClickHouse.
