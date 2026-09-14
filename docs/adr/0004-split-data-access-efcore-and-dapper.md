# ADR-0004 — Split data access: EF Core for the control plane, Dapper for the hot path

**What this is:** the decision the specification calls its most important technical note — a correction of the original "Minimal API + EF Core" idea.
**Who it is for:** anyone touching the resolve query, and anyone tempted to "just use the DbContext" in the edge.

## Status

Accepted

## Date

Decided: in the specification ([§B.4 ADR-004](../zadanie.md#adr-004--prístup-k-dátam-ef-core-pre-control-plane-dapper-pre-hot-path)) · Recorded: 2026-09-11

## Context

The original proposal was ".NET 10 Minimal API + EF Core engine". The first half is right. The second is right **for half of the system**.

Why EF Core does not belong on the resolve path:

- Even with `AddDbContextPool` and compiled queries, EF Core has measurable overhead for entity materialisation and change tracking. Microsoft's own benchmark shows pooling cutting latency from ~702 µs to ~350 µs and compiled queries to ~564 µs — hundreds of microseconds on an operation that has 8 ms in total and should spend as little of it as possible in the database layer.
- EF Core's Native AOT support (via precompiled queries) is labelled by Microsoft as highly experimental and "not yet suited for production use". With EF Core in the request pipeline the road to a production AOT edge is effectively closed — and the edge is exactly where AOT would pay ([ADR-0012](0012-native-aot-deferred-to-v2.md)).
- Resolve needs exactly one query: `SELECT … FROM links WHERE domain_id = $1 AND slug = $2`. That does not need an ORM.

Why EF Core does belong in the control plane: CRUD over links, domains, apps and tenants is where productivity beats microseconds, and EF Core 10 brings things the design uses directly — `LeftJoin`/`RightJoin`, `ExecuteUpdate` over JSON paths (counters without materialising the entity), named query filters (multi-tenancy and soft delete side by side), complex types mapped to JSON, and literal redaction in logs by default. Npgsql 10.0.x has full JSONB mapping and PG 18 support including `Guid.CreateVersion7()` → `uuidv7()`.

## Decision

| Path | Technology | Reason |
|---|---|---|
| Resolve (`GET /{slug}`) | **Dapper / raw Npgsql** + `HybridCache` | latency, AOT compatibility, one query |
| Click-event write | **`NpgsqlBinaryImporter` (`COPY`)** in batches | throughput |
| Control-plane CRUD | **EF Core 10** | productivity, migrations, audit |
| Attribution service | **EF Core 10** | transactional, low frequency |
| Reports | **Dapper** over rollup tables / ClickHouse | hand-shaped SQL |

One domain schema, two access mechanisms. **EF Core migrations are the single source of truth for the schema**; the Dapper queries are covered by integration tests that fail when the schema drifts.

## Consequences

- Positive: the edge process has no EF Core dependency (`src/Dle.Persistence.Fast`), so it stays small, fast and AOT-ready; the control plane keeps the migration story and the productivity.
- Negative: two ways of talking to the same tables. A column added in a migration must be picked up by hand in the Dapper query and in the covering index (`ix_links_resolve`, [architecture/data-model.md](../architecture/data-model.md)).
- Negative: the safety net for that drift is the integration suite — **97 tests, written, never run on the build machine** (no Docker). Until CI runs them with `DLE_TESTS_REQUIRE_DOCKER=1`, drift between the migration and the hot-path SQL is caught by review only.
- Verification: `Dle.Persistence` and `Dle.Persistence.Fast` are separate projects; the edge host references only the latter.

## Alternatives considered

| Alternative | Why it lost |
|---|---|
| EF Core everywhere | Hundreds of microseconds per resolve for no benefit; closes the AOT path for the edge |
| Dapper everywhere | Loses migrations as the schema source of truth and the productivity in the control plane, where a few microseconds are irrelevant |
| Two schemas (one per access path) | Doubles the maintenance for a system whose whole point is one database |

## References

- [docs/zadanie.md §B.4 ADR-004](../zadanie.md#adr-004--prístup-k-dátam-ef-core-pre-control-plane-dapper-pre-hot-path)
- [docs/zadanie.md §C.3.1](../zadanie.md#c31-hot-path--resolve-endpoint), [§C.3.2](../zadanie.md#c32-dávkový-zápis-eventov) — the hot path and the batch writer
- `src/Dle.Persistence`, `src/Dle.Persistence.Fast`, `tests/Dle.IntegrationTests`
