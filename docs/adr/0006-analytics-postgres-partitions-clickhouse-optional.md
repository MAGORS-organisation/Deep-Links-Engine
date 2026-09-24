# ADR-0006 — Analytics: PostgreSQL partitions by default, ClickHouse opt-in

**What this is:** where the click stream lives on day one and where it can move when it outgrows that.
**Who it is for:** operators deciding whether they need ClickHouse, and developers touching `IClickAnalyticsStore`.

## Status

Accepted

**Status note (2026-09-24).** The ClickHouse provider is not wired end to end: the edge still writes clicks to PostgreSQL, the ClickHouse schema script is never applied, and selecting the provider replaces the PostgreSQL retention and partition maintenance with a no-op. Moving to ClickHouse is therefore not yet "a provider switch"; the provider is experimental and should not be enabled. Separately, raw click events are dropped after **30 days** by default (`Dle:Privacy:Retention:RawDays` = 30, applied by the control plane's retention job), not the 180 days below. See [Known gaps](../../README.md#known-gaps).

## Date

Decided: in the specification ([§B.4 ADR-006](../zadanie.md#adr-006--analytika-postgres-partície-ako-default-clickhouse-ako-opt-in)) · Recorded: 2026-09-11

## Context

A self-hoster does not want to run two databases on the first day. But the click stream is the one table that grows without bound — [NFR-08](../zadanie.md#a5-nefunkčné-požiadavky) plans for 10¹⁰ events at 180 days retention — and analytical queries over it are a different workload from the point lookups the rest of the system does. Dub went exactly this way: it started on Redis sorted sets, outgrew them and moved to ClickHouse-based Tinybird with ~100× faster queries. The difference here is knowing that in advance.

## Decision

The click stream is stored **primarily in a partitioned PostgreSQL table** (`click_events`, range-partitioned by `occurred_at`, BRIN index, daily partitions managed by `pg_partman` with 7 days pre-created and 180 days retention — [architecture/data-model.md](../architecture/data-model.md)). Installations above roughly **50 million events per month** can enable an optional **ClickHouse** sink (Apache 2.0). Both sit behind one abstraction, `IClickAnalyticsStore`, designed in from the start so the migration is a provider switch, not a rewrite: `src/Dle.Analytics.Postgres` is the default implementation, `src/Dle.Analytics.ClickHouse` the optional one.

## Consequences

- Positive: one `docker compose up` gives a complete, functional instance ([NFR-09](../zadanie.md#a5-nefunkčné-požiadavky)); retention is a `pg_partman` setting, not a cron job someone forgets.
- Positive: logical replication from PostgreSQL is the ready path to feed ClickHouse without an ETL layer ([ADR-0003](0003-postgresql-as-primary-store.md)).
- Negative: report queries over months of data in PostgreSQL will be slower than ClickHouse; rollup tables written by the rollup worker exist to make the dashboard cheap regardless.
- Negative: two implementations of one interface to keep in step.
- Negative: the rollup schema cannot live in the EF Core migration set, because an instance running the ClickHouse provider has no use for it. It ships as `src/Dle.Analytics.Postgres/Sql/001_analytics_rollups.sql`, every statement idempotent, and the control plane applies it at start-up (`PostgresAnalyticsSchema`); an instance whose database role may not create tables gets a logged failure and a runbook entry rather than a silent one.
- Verification: both providers build and are covered by unit tests; the partition DDL and `pg_partman` configuration are in the generated migration, which the integration suite applies to and rolls back on a live PostgreSQL 18 in CI. The rollup schema and the jobs that read it are covered by the analytics integration tests.

## Alternatives considered

| Alternative | Why it lost |
|---|---|
| ClickHouse from day one | A second database for every installation, including the ones that will never see a million clicks |
| TimescaleDB | Dual licence — Apache core plus the Timescale Licence for the advanced features, which forbids offering the product as a DBaaS. For an OSS product with a possible hosted variant, a needless complication |
| DuckDB | Excellent for embedded reporting in small installations; noted as a candidate **third** optional provider, not for v1 |
| Redis sorted sets (Dub's first approach) | Known to be outgrown; there is no reason to repeat the detour |

## References

- [docs/zadanie.md §B.4 ADR-006](../zadanie.md#adr-006--analytika-postgres-partície-ako-default-clickhouse-ako-opt-in)
- [docs/zadanie.md §B.5.3](../zadanie.md#b53-data-plane--eventy-partitionované) — the partitioned table
- [docs/zadanie.md §C.3.2](../zadanie.md#c32-dávkový-zápis-eventov) — the batch writer that feeds it
- `src/Dle.Analytics.Postgres`, `src/Dle.Analytics.ClickHouse`
