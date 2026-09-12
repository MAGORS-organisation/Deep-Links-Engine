# ADR-0005 — Cache: HybridCache with Valkey as L2

**What this is:** the cache in front of the resolve query, and why the L2 is Valkey rather than Redis.
**Who it is for:** operators sizing the cache tier, and anyone asking whether they may point the connection string at Redis.

## Status

Accepted

## Date

Decided: in the specification ([§B.4 ADR-005](../zadanie.md#adr-005--cache-hybridcache--valkey)) · Recorded: 2026-09-11

## Context

Only 1–5 % of resolves should reach PostgreSQL ([ADR-0003](0003-postgresql-as-primary-store.md)). A viral link means thousands of simultaneous misses for the same key; a naive cache-aside turns that into thousands of identical queries (a stampede). A domain change must invalidate every link under it across all edge replicas. And whatever runs as L2 ships inside the Compose and Helm bundles, so its licence is the operator's problem as much as ours.

## Decision

`Microsoft.Extensions.Caching.Hybrid` (**HybridCache**, GA since .NET 9, on the version that tracks the platform) with an in-process **L1** and **Valkey** as **L2**.

- HybridCache because of built-in **stampede protection** (concurrent requests for one key are coalesced into one database query), **tag-based invalidation** (invalidate all of a tenant's links when its domain changes) and one API instead of hand-written cache-aside.
- Valkey because Redis moved to RSALv2/SSPL in March 2024 and, from Redis 8 (1 May 2025), back to **AGPLv3**. Valkey is the Linux Foundation fork of the last BSD Redis, licensed **BSD-3-Clause**, protocol-compatible, backed by AWS, Google and Oracle. Switching back to Redis is a one-line connection-string change.

## Consequences

- Positive: a viral link costs one query, not thousands; degraded modes are well defined — PostgreSQL down → resolve continues from L1+L2 for cached links; Valkey down → resolve continues from L1 + PostgreSQL with higher latency and the `cache_l2_down` signal ([§D.6](../zadanie.md#d6-chaos-a-odolnosť), [NFR-06](../zadanie.md#a5-nefunkčné-požiadavky)).
- Positive: the L2 is an `IDistributedCache`; any RESP-compatible server works.
- Negative: one more process in the Compose bundle (Profile A runs `valkey:8`); the L1 is per-replica, so invalidation latency across replicas is the L2's job.
- **On the licence argument after ADR-0011.** This repository is MIT ([ADR-0011](0011-license-mit.md)), not AGPL, so the question "would AGPL Redis contaminate our licence?" is moot — and the specification is careful to say it never was a contamination question: an application that merely connects to an AGPL Redis over the network does not change its own licence. The two real problems remain and are why Valkey stays: (a) many companies have a blanket "no AGPL in the stack" rule and will not discuss it, and (b) **we distribute the L2 as part of our `docker compose` and Helm bundle**, where the boundary is much less clear-cut. Valkey is about not shipping AGPL software in the bundle and removing that conversation entirely; it is not a claim about contamination.
- Verification: the cache path is exercised by the unit and contract suites against in-memory implementations; the Valkey-backed behaviour is in the integration suite, which has not run on the build machine.

## Alternatives considered

| Alternative | Why it lost |
|---|---|
| Redis 8 | AGPLv3 since 1 May 2025 — shipping it in the bundle raises the two problems above. Still usable by an operator who has it: same protocol |
| Garnet (Microsoft Research) | MIT, written in .NET, RESP-compatible, sub-300 µs P99.9, Aspire integration — technically the best fit for the stack, but without publicly documented production track record. **Recommendation: support as an alternative provider (it is just `IDistributedCache`); do not build v1 on it** |
| Hand-written cache-aside over `IDistributedCache` | No stampede protection, no tags; rewriting what HybridCache already provides |

## References

- [docs/zadanie.md §B.4 ADR-005](../zadanie.md#adr-005--cache-hybridcache--valkey); [§B.4 ADR-011](../zadanie.md#adr-011--licencia-agpl-30--cla) for the precise licence reasoning
- [docs/zadanie.md §B.6.1](../zadanie.md#b61-rozlíšenie-kliku) — `GetOrCreateAsync("lnk:{host}:{slug}")` in the resolve flow
- [../deploy/README.md](../../deploy/README.md) — the `valkey:8` service in Profile A
