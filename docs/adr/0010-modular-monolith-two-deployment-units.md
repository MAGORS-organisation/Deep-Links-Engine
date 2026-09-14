# ADR-0010 — Modular monolith with two deployment units

**What this is:** why one solution, two processes and one database — and when a third process would be justified.
**Who it is for:** anyone proposing a service split, and operators wondering what they are actually running.

## Status

Accepted

## Date

Decided: in the specification ([§B.4 ADR-010](../zadanie.md#adr-010--modulárny-monolit)) · Recorded: 2026-09-11

## Context

The specification estimates the whole product at 160–185 person-days. Microservices would spend 20–30 % of that on infrastructure nobody needs — service discovery, inter-service auth, distributed tracing across hops, per-service pipelines. The only component that genuinely needs independent scaling is the edge resolver, and it is already separable.

## Decision

**One .NET solution, two deployment units, one database**, with modules that have explicit boundaries: `Links`, `Routing`, `Attribution`, `Analytics`, `Abuse`, `Identity`.

| Unit | Port | Contains (component IDs from [§B.3](../zadanie.md#b3-komponenty)) | Scales with |
|---|---|---|---|
| `dle-edge` | `:8080` | C-01 Edge Resolver, C-02 Well-Known Server, C-03 Interstitial Renderer | traffic (stateless, N replicas; Helm HPA) |
| `dle-control` | `:8081` | C-04 Control Plane API, C-05 Admin UI, C-06 Attribution Service, C-07…C-12 background workers | number of operators |

Background workers (domain verifier, event ingest, rollup, webhook dispatcher, abuse, key management) run inside the control process with **leader election over a PostgreSQL advisory lock** — no additional coordination system.

**When to revisit:** if Event Ingest starts to dominate resource use, split it out as a third unit. The module boundaries are drawn so that this is moving a project, not rewriting one.

## Consequences

- Positive: one build, one migration, one backup; local development is `docker compose up` (Profile A) and a Kubernetes deployment is a Helm chart with two Deployments (Profile B) — [../deploy/README.md](../../deploy/README.md).
- Positive: the edge has a minimal dependency set (`Dle.Persistence.Fast`, no EF Core) and is the AOT candidate ([ADR-0012](0012-native-aot-deferred-to-v2.md)).
- Negative: module boundaries inside a solution are enforced by project references and review, not by a network. The [SHARED-KERNEL.md](../dev/SHARED-KERNEL.md) contract exists for that reason.
- Negative: a control-plane deployment restarts the workers; leader election makes that safe but not free.
- Verification: the solution is 14 projects; both hosts start on the build machine; the edge host references only the fast persistence path. The leader-election behaviour under concurrent control replicas is in the integration suite, which has not run here.

## Alternatives considered

| Alternative | Why it lost |
|---|---|
| Microservices per module | 20–30 % of the budget on plumbing; the one scaling need is already covered by the edge/control split |
| A single process for everything | Edge and control have different scaling, different rate limits and different authentication (SDK keys vs operator sessions); mixing them puts the console's attack surface on the hot path |
| Separate coordination service for workers (Redis locks, etcd) | A PostgreSQL advisory lock does the job with no new dependency |

## References

- [docs/zadanie.md §B.4 ADR-010](../zadanie.md#adr-010--modulárny-monolit)
- [docs/zadanie.md §B.3](../zadanie.md#b3-komponenty) — the component table; [§B.8](../zadanie.md#b8-topológia-nasadenia) — Profiles A and B; [§C.1](../zadanie.md#c1-štruktúra-riešenia) — the solution layout
- [architecture/README.md](../architecture/README.md), [architecture/context.md](../architecture/context.md)
