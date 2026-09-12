# ADR-0001 — Runtime: .NET 10 LTS

**What this is:** the choice of runtime and the support horizon it buys.
**Who it is for:** whoever plans the next runtime upgrade, and anyone asking why the solution is not on the newest .NET.

## Status

Accepted

## Date

Decided: in the specification ([§B.4 ADR-001](../zadanie.md#adr-001--runtime-net-10-lts)) · Recorded: 2026-09-11

## Context

The engine is meant to be self-hosted for years by teams that do not want to chase runtime releases. The specification sets a project lifetime of at least three years. .NET ships on a yearly cadence alternating between LTS (three years of support) and STS (two years). Choosing the wrong lane means an operator's supported runtime expires before the product does.

## Decision

Build against **.NET 10**, GA 11 November 2025, an LTS release supported until **14 November 2028**. The upgrade to .NET 12 LTS (expected November 2027) is a planned task, not an emergency.

## Consequences

- Positive: the support window exceeds the project horizon; operators get security patches without a major upgrade for three years.
- Positive: .NET 10 brings the pieces the rest of the design depends on — native Minimal API validation ([ADR-0002](0002-minimal-apis-vertical-slices.md)), `HybridCache` on a platform-tracked version ([ADR-0005](0005-hybridcache-and-valkey.md)), EF Core 10 and Npgsql 10 with PostgreSQL 18 support ([ADR-0004](0004-split-data-access-efcore-and-dapper.md)), and the post-quantum primitives referenced by [ADR-0013](0013-crypto-agility-from-day-one.md).
- Negative: features that land in .NET 11 (STS, GA 10 November 2026) are off the table until .NET 12.
- Verification: the whole solution — 14 projects — builds with `-warnaserror` at 0/0 and `TreatWarningsAsErrors` set in `Directory.Build.props` (see [§C.1](../zadanie.md#c1-štruktúra-riešenia)).

## Alternatives considered

| Alternative | Why it lost |
|---|---|
| .NET 11 | GA only on 10 November 2026 and an **STS** release with two years of support. For an OSS product that self-hosters run for years, STS is the wrong lane |
| .NET 8 LTS | Supported to November 2026 — shorter than the project horizon, and it lacks the .NET 10 features listed above |

## References

- [docs/zadanie.md §B.4 ADR-001](../zadanie.md#adr-001--runtime-net-10-lts); summary row in [§0.3](../zadanie.md#03-kľúčové-technické-rozhodnutia-zhrnutie)
- [docs/zadanie.md §C.2](../zadanie.md#c2-technologický-zásobník) — the technology stack
- `Directory.Build.props`, `Directory.Packages.props` (central package management)
