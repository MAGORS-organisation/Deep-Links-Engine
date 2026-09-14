# ADR-0012 — Native AOT deferred to v2

**What this is:** why the edge is built to be AOT-compilable but is not AOT-compiled in v1.
**Who it is for:** whoever picks up the v2 edge profile, and anyone about to add reflection to `Dle.Edge`.

## Status

**Accepted, deferred** — the constraint (AOT-readiness) is in force now; the AOT build is scheduled for v2.

## Date

Decided: in the specification ([§B.4 ADR-012](../zadanie.md#adr-012--native-aot)) · Recorded: 2026-09-11

## Context

Native AOT promises faster start-up and lower memory — attractive for an edge tier that autoscales ([§B.8](../zadanie.md#b8-topológia-nasadenia), HPA 3–20 pods). Two things stand in the way. EF Core supports AOT only experimentally (Microsoft explicitly calls it unsuitable for production), so any process containing EF Core is a dead end for AOT. And the published gains — for example a 450 ms → 50 ms start-up figure — come from third-party sources and have not been verified for this workload.

## Decision

- **Not in v1.**
- The edge resolver is designed **AOT-ready** from the start: Dapper instead of EF Core ([ADR-0004](0004-split-data-access-efcore-and-dapper.md)), source-generated `System.Text.Json` serialisation, no reflection-based binding or DI tricks.
- The AOT build is enabled in v2 as a **separate publish profile** for the edge only, after an in-house benchmark justifies it.

## Consequences

- Positive: nothing in `Dle.Edge` blocks a later AOT build; the cost of keeping that true is a code-review rule, not a rewrite.
- Positive: v1 ships on the well-trodden JIT path with no trimming surprises.
- Negative: v1 edge start-up and memory are what the JIT gives; autoscale reaction time is bounded by that.
- Negative: AOT-adjacent settings already bite. The whole solution sets `InvariantGlobalization` in `Directory.Build.props` (a trimming-friendly choice), and under it the NFKC normalisation of custom slugs was a **silent no-op** — the homoglyph defence never ran until the test suite caught it. The v2 AOT profile must expect more of this class of surprise and be tested for behaviour, not only for "it compiles".
- Verification: the edge host builds and starts; there is no AOT publish profile yet and no start-up benchmark has been run. The claim "AOT-ready" is by construction and review, not by an AOT build.

## Alternatives considered

| Alternative | Why it lost |
|---|---|
| AOT for both hosts in v1 | Impossible with EF Core in the control plane |
| AOT for the edge in v1 | No benchmark to justify the trimming risk; the specification asks for a measured case first |
| ReadyToRun (partial pre-compilation) as a middle path | Not ruled out for v1.x; it is a publish flag, not a design decision, and does not need an ADR |

## References

- [docs/zadanie.md §B.4 ADR-012](../zadanie.md#adr-012--native-aot)
- [docs/zadanie.md §B.4 ADR-004](../zadanie.md#adr-004--prístup-k-dátam-ef-core-pre-control-plane-dapper-pre-hot-path) — the EF Core / AOT constraint
- `src/Dle.Edge`, `src/Dle.Persistence.Fast`
