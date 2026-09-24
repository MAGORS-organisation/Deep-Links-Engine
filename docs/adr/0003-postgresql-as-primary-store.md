# ADR-0003 — PostgreSQL 18 as the primary store

**What this is:** the SQL-versus-NoSQL decision, argued from the workload rather than from fashion.
**Who it is for:** anyone proposing a second database, and operators asking which PostgreSQL version they need.

## Status

Accepted

## Date

Decided: in the specification ([§B.4 ADR-003](../zadanie.md#adr-003--sql-vs-nosql-postgresql-18-ako-primárne-úložisko)) · Recorded: 2026-09-11

## Context

The specification was explicitly asked to settle this, so it starts from what the system actually does:

| Operation | Frequency | Shape |
|---|---|---|
| Slug resolution | 10⁴–10⁶ / day, thousands/s at peak | **point lookup by one key**, 100 % read |
| Click-event write | the same | append-only, batchable, loss-tolerant |
| Link CRUD | tens–thousands / day | relational, transactional, with references |
| Reports | hundreds / day | aggregations over time series |
| Install matching | thousands / day | lookup + write, transactional |

The target user is a team that runs `docker compose up` and does not want to operate a cluster.

## Decision

**PostgreSQL 18 is the only mandatory store.** No NoSQL system is used in the core. ClickHouse is an optional analytics sink ([ADR-0006](0006-analytics-postgres-partitions-clickhouse-optional.md)), never a dependency. Minimum supported version **PG 16** (PG 14 reaches end of life on 12 November 2026); recommended **PG 18** because of native `uuidv7()`.

Why PostgreSQL wins, in the specification's own order:

1. **A point lookup is not a reason for NoSQL.** A primary-key lookup is sub-millisecond in PostgreSQL, and the cache in front of it ([ADR-0005](0005-hybridcache-and-valkey.md)) means only 1–5 % of requests reach the database. At ≥ 95 % cache hit rate the "NoSQL is faster by key" argument is irrelevant.
2. **Writes are not the problem.** Clicks are written **in batches, asynchronously** with `COPY` (`NpgsqlBinaryImporter`), tens of thousands of rows per second, not one insert per click.
3. **The business half is relational.** Tenants, domains, apps, links, keys, audit — entities with references and invariants. A schemaless store keeps those only by discipline nobody has.
4. **PG 18 ships `uuidv7()` natively** — time-ordered UUIDs for the event tables, so inserts go to the end of the B-tree instead of causing random page splits, and BRIN indexes become viable (orders of magnitude smaller than B-tree on append-only data).
5. **Declarative partitioning + `pg_partman`** handle click-stream retention (daily partitions, automatic `DETACH`/`DROP`) without another system.
6. **Logical replication** is the ready-made path to stream into ClickHouse later without ETL.

## Consequences

- Positive: one system to back up, monitor and upgrade; transactions and foreign keys where the domain needs them; `jsonb` where it needs flexibility (`routing_rules`, `og_meta`, `settings`, `evidence`).
- Negative: an installation above ~50 M events/month will want ClickHouse for reports; that path is designed in ([ADR-0006](0006-analytics-postgres-partitions-clickhouse-optional.md)) but is a second system.
- Negative: PG 16/17 lack `uuidv7()`, so the migration carries a shim function for older servers.
- Verification (as of 2026-09-24): the integration suite applies the EF Core migration (extensions, uuidv7 shim, partitions) to PostgreSQL 18 in Testcontainers in CI, and rolls it back. The test image is the stock one, which has no `pg_partman`, so it is the migration's fallback partition maintenance that runs there, not the `pg_partman` handover. CI sets `DLE_TESTS_REQUIRE_DOCKER=1`, so a missing Docker fails the job instead of skipping the suite; the current result is in the CI summary.

## Alternatives considered

| Alternative | Why it lost |
|---|---|
| DynamoDB | Horizontal write scaling and single-digit ms at any size — but **proprietary and AWS-only**, in direct conflict with self-hosting ([NFR-11](../zadanie.md#a5-nefunkčné-požiadavky)) |
| Cassandra / ScyllaDB | Self-hostable, TWCS compaction suits TTL event data; the price is cluster topology, repair and quorum tuning — wrong for a `docker compose up` audience |
| MongoDB | Flexible schema for heterogeneous metadata — which `jsonb` provides while keeping transactions and foreign keys |

## References

- [docs/zadanie.md §B.4 ADR-003](../zadanie.md#adr-003--sql-vs-nosql-postgresql-18-ako-primárne-úložisko)
- [docs/zadanie.md §B.5](../zadanie.md#b5-dátový-model) — the data model; [architecture/data-model.md](../architecture/data-model.md)
- `src/Dle.Persistence` (EF Core, migrations), `src/Dle.Persistence.Fast` (Dapper, `COPY`)
