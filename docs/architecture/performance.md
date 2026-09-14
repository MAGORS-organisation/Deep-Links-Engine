# Performance and resilience

**What this is:** the numbers the engine is held to — NFR-01…NFR-08, the per-step resolve budget, the k6 load profile and the chaos matrix — and an honest statement of which of them have been measured.
**Who it is for:** whoever runs the first load test, operators sizing an instance, and reviewers of any change on the hot path.

## Targets ([§A.5](../zadanie.md#a5-nefunkčné-požiadavky), NFR-01…NFR-08)

| ID | Category | Requirement | Measured by | Status on the build machine |
|---|---|---|---|---|
| NFR-01 | Latency | Resolve **p50 ≤ 8 ms, p95 ≤ 25 ms, p99 ≤ 50 ms** server-side (no network) at cache hit | k6 / NBomber, OTel histogram | designed to ~5.5 ms p50; **not measured under load** |
| NFR-02 | Latency | Cache miss (PostgreSQL lookup) **p99 ≤ 120 ms** | as above | not measured; depends on the covering index being index-only ([data-model.md](data-model.md#the-covering-index)) |
| NFR-03 | Throughput | **≥ 5 000 req/s per instance** (4 vCPU, 8 GB) at ≥ 95 % cache hit rate | load test | not measured |
| NFR-04 | Scaling | Horizontal, stateless; adding an instance needs no restart of the others | test | by construction (edge holds no state); HPA in Helm, not exercised here |
| NFR-05 | Availability | **99.9 %** for the resolve path; **99.5 %** for the control plane | SLO, error budget | operational — nothing to verify before production |
| NFR-06 | Degradation | PostgreSQL down → resolve from cache; cache down → resolve from PostgreSQL; analytics down → events dropped, response never blocked | chaos test | unit-tested at the component level; chaos matrix below not run |
| NFR-07 | RPO / RTO | RPO ≤ 5 min (control plane), RTO ≤ 30 min | DR exercise | operator's backup procedure ([../self-hosting](../self-hosting)) |
| NFR-08 | Volume | 10⁹ links, 10¹⁰ click events at 180 days retention | capacity model | schema designed for it (Snowflake ids, partitions, BRIN); not loaded to that scale |

Sizing from [§B.8](../zadanie.md#b8-topológia-nasadenia): Profile A (2 vCPU / 4 GB / 20 GB, 2 edge replicas) handles about **1 000 req/s**; NFR-10 says the base instance must run on exactly that. Profile B scales the edge 3–20 pods.

## The resolve budget ([§B.6.1](../zadanie.md#b61-rozlíšenie-kliku))

| Step | Budget | What can blow it |
|---|---|---|
| Normalisation | 0.2 ms | — |
| Cache lookup | 0.5 ms | L1 cold after deploy → L2 round trip; Valkey down → PostgreSQL on every L1 miss |
| Classification (UA parsing + GeoIP MMAP) | 1.5 ms | a GeoIP file that is not memory-mapped; reverse-DNS crawler verification done synchronously instead of from the cached verdict |
| Rule evaluation | 0.3 ms | more than a handful of rules with version predicates — the validator caps a link at 50 |
| Response generation | 1.0 ms | rendering the interstitial without pre-compiled templates |
| Kestrel overhead | ~2 ms | — |
| **Total** | **~5.5 ms p50** | 8 ms is the target, so the headroom is 2.5 ms |

Two rules protect the budget: **no outbound call to anything on the hot path** (GeoIP is a local file, [NFR-14](../zadanie.md#a5-nefunkčné-požiadavky)), and **telemetry never waits** — the click event goes into a bounded channel with `TryWrite` and is dropped, counted, when the channel is full ([observability.md](observability.md)).

## Load profile ([§D.5](../zadanie.md#d5-záťažový-profil))

The k6 script in `tests/load/resolve.js` is **written and has never been run** on the build machine. It encodes:

```javascript
export const options = {
  scenarios: {
    steady:  { executor: 'constant-arrival-rate', rate: 2000, timeUnit: '1s',
               duration: '10m', preAllocatedVUs: 200 },
    spike:   { executor: 'ramping-arrival-rate', startTime: '10m',
               stages: [{ target: 12000, duration: '30s' },
                        { target: 12000, duration: '2m' },
                        { target: 2000,  duration: '1m' }] },
  },
  thresholds: {
    'http_req_duration{scenario:steady}': ['p(95)<25', 'p(99)<50'],
    'http_req_failed': ['rate<0.001'],
  },
};
```

| Aspect | Value |
|---|---|
| Steady state | 2 000 req/s for 10 minutes |
| Spike | ramp to 12 000 req/s in 30 s, hold 2 min, back to 2 000 over 1 min |
| Traffic mix | 80 % "hot" links (top 100), 15 % random existing, 5 % non-existent (exercises the 404 path and its separate rate budget) |
| Crawler share | 12 % of requests with a crawler UA (exercises the OG preview path) |
| Pass criteria | steady p95 < 25 ms, p99 < 50 ms; failures < 0.1 %; cache hit rate ≥ 95 %; **`dle_click_events_dropped_total` = 0** |

Run it against a Profile A instance first; the numbers that come back are the first real evidence for NFR-01 and NFR-03 and belong in the release checklist ([../operations](../operations)).

## Chaos and resilience ([§D.6](../zadanie.md#d6-chaos-a-odolnosť))

| Scenario | Expected behaviour | Run here |
|---|---|---|
| PostgreSQL unavailable | resolve works from cache (L1 + L2) for cached links; uncached links return `503`; control plane returns `503`; **no `500`** | no |
| Valkey unavailable | resolve works from L1 + PostgreSQL with higher latency; `cache_l2_down` metric | no |
| Analytics layer unavailable | events are dropped once the buffer fills; resolve unaffected; alert | component-level unit test of the channel; not as chaos |
| Disk full | event writes fail; resolve works; alert | no |
| GeoIP file missing or corrupt | `country = null`; geo-dependent rules fall through to `default`; alert | rule fall-through unit-tested |
| 10× traffic increase | HPA scales out; p99 must not exceed **200 ms** during scaling | no |

## Device matrix ([§D.2.1](../zadanie.md#d21-povinná-manuálna-matica-pred-release-8-kombinácií))

Performance on the server is only half of "does the link work". The eight mandatory manual combinations (iOS and Android, installed and not installed, Safari/Chrome and in-app webview) are not automatable by design ([§D.2.2](../zadanie.md#d22-prečo-sa-to-nedá-zautomatizovať)) and are **pending**; the checklist lives in [../operations](../operations).

## Summary of evidence

| Claim | Evidence today |
|---|---|
| The hot path has no EF Core, no reflection, no outbound calls | project references and code review; 850 security tests confirm no egress on the resolve path at the HTTP level |
| Each pipeline step is cheap | unit tests per step in the domain tier (≥ 90 % coverage) |
| The budget holds end to end | **none yet** — the k6 profile has not run |
| The degraded modes behave as specified | unit tests for fall-through; **no chaos run** |
| The covering index is used index-only | **none yet** — needs `EXPLAIN (ANALYZE, BUFFERS)` against a live PostgreSQL after `autovacuum_vacuum_scale_factor = 0.02` |

The specification's own verdict in [§F.3](../zadanie.md#f3-gono-go-odporúčanie) applies: the design is sound; the numbers are targets until the load run says otherwise.
