# Load profile — resolve path

The §D.5 profile, as k6 scripts. **Nothing in this directory has been run.** k6 is not installed on
the machine these files were written on, and the profile needs a deployed edge with PostgreSQL and
Valkey behind it — a load test against a substituted store measures the substitute. The files are
committed so that the profile is reviewable, versioned and reproducible, not because a number in this
repository has been produced by them.

| File | What it is |
|---|---|
| `resolve.js` | The §D.5 profile: two scenarios, the traffic mix, the thresholds. |
| `seed.js` | Creates the link corpus the profile needs and writes `corpus.json`. Node 24. |
| `corpus.json` | Produced by `seed.js`. Not committed: it names one deployment's links. |

## Status: it cannot pass as written

As of 2026-09-24 a run of this profile against a default-configured instance fails, for three independent reasons. None of them says anything about the latency of the resolve path.

1. **Expected 404s count as failures.** k6 marks every response with a status of 400 or above as failed unless the script says otherwise, and `resolve.js` does not (it never calls `http.setResponseCallback`). The 5 % miss stream alone therefore puts `http_req_failed` near 0.05 against its `rate<0.001` threshold, even though `dle_correct_outcome` counts those 404s as correct.
2. **The default edge rate limits throttle the generator.** Limits are keyed by client network (IPv4 /24, IPv6 /48; `Dle:RateLimits:Edge` in `src/Dle.Edge/appsettings.json`): 600 successful resolves a minute, and a 404 budget of 20 a minute with a burst of 40, after which the network is shadow banned for 15 minutes — every request from it, hot links included, is answered `404` without a lookup. At 2 000 req/s with 5 % misses, a generator on one network uses up both budgets in under a second; from then on nearly every request is answered `429` or with a shadow-ban `404`. The shadow ban answers `404`, not `429`, so `dle_rate_limited` does not show it.
3. **The seeder stops at the control plane's link-create limit.** `Dle:RateLimits:LinkCreatePerMinute` is 60 per API key (10 while the tenant is younger than 7 days); `seed.js` sends 16 creates in parallel and aborts on any status other than 200, 201 or 409, so the first `429` ends it long before 10 000 links exist.

A passing run needs a response callback that treats the expected 404s as expected, rate limits configured for the run and reported with the result, and a seeder that stays under the create limit. None of that is in this directory; the scripts are as written. The [Reading a failure](#reading-a-failure) table below assumes a run that can pass: under default limits, many 404s on existing slugs are the shadow ban rather than a stale corpus.

## Running it

```bash
# 1. Seed. Needs a control-plane API key and a verified domain.
node seed.js \
  --control https://control.example.com \
  --api-key  dle_ab12cd34_.................. \
  --domain-id 5d7c9e2b-4f16-4a8e-9c2d-8f4c1f0a77b1 \
  --host      link.example.com \
  --total 10000 --hot 100

# 2. Run. Thirteen and a half minutes: ten of steady state, then the spike.
k6 run -e BASE_URL=https://link.example.com resolve.js
```

`seed.js` sends an `Idempotency-Key` per link, so re-running it against a partially seeded instance
finishes the job instead of producing ten thousand conflicts. Every link it creates is prefixed
`k6load`, which is also how they are found and deleted afterwards.

## The profile

```
steady   constant-arrival-rate   2000 req/s   10 minutes
spike    ramping-arrival-rate    0 → 12000/s over 30 s, held 2 min, down to 2000/s over 1 min
```

Traffic mix, from §D.5:

| Share | What |
|---|---|
| 80 % | one of the hundred hot links |
| 15 % | a random link from the whole corpus |
| 5 % | a slug that does not exist |
| 12 % (across all of the above) | a crawler user agent |

The 5 % miss stream is not noise to be filtered out. It exercises the negative cache, the 404 page
and the anti-enumeration budget of §E.9, all of which are on the resolve path and all of which a
profile without misses would leave unmeasured. The 12 % crawler share is there for the same reason:
a confirmed crawler is served an Open Graph document rather than a redirect (ADR-009, TC-106), which
is the more expensive of the two paths.

## Acceptance criteria

These are the release gates from §D.5 and §D.7 criterion 2. A run that misses any of them is a
release that does not go out.

**k6 decides these.** They are `thresholds` in `resolve.js`, so the run exits non-zero on its own.

| Criterion | Threshold | Source |
|---|---|---|
| Steady-state median tail | `p(95) < 25 ms` | §D.5 |
| Steady-state far tail | `p(99) < 50 ms` | §D.5 |
| Failure rate | `http_req_failed < 0.001` | §D.5 |
| Correct outcome per request | `dle_correct_outcome > 0.999` | this profile |
| Hot-link latency | `p(95) < 25 ms`, `p(99) < 50 ms` | NFR-01 |
| Tail during the spike | `p(99) < 200 ms` | §D.6, "10× nárast prevádzky" |

`dle_correct_outcome` is not in §D.5 and is added deliberately. Latency thresholds alone can be met
by an instance that answers 404 to everything, and a load test that can pass while the product is
broken is worse than no load test. Every response is checked against what that request should have
produced: a redirect or an interstitial for a browser, an Open Graph document for a crawler, 404 or
429 for a slug that does not exist, and never a 301 (ADR-009) or a 5xx.

**k6 cannot decide these.** They are read from the instance's own metrics after the run.

| Criterion | Where | Source |
|---|---|---|
| Cache hit rate ≥ 95 % | `dle_cache_hit_ratio{level="l1"}` + `{level="l2"}` on the edge | §D.5 |
| `dle_click_events_dropped_total` = 0 | edge, must not have moved during the run | §D.5 |
| No 5xx in the edge log | edge | §D.6: "žiadny 500" |

Record the before and after value of `dle_click_events_dropped_total` rather than its rate. It is a
counter, the bounded channel drops on `DropWrite` under back pressure (NFR-06), and the whole point
of the criterion is that the trade never had to be made at this load.

A cache hit rate below 95 % with the profile's own mix almost always means the corpus is too small
or the run too short rather than that the cache is broken: 80 % of traffic goes to a hundred links,
so the steady state should be nearly all L1. Check the corpus size before changing a cache setting.

## Reading a failure

| Symptom | Where to look first |
|---|---|
| `p(95)` fine, `p(99)` over | `dle_cold_latency` — the miss path is reaching PostgreSQL more than it should; check the covering index is doing an index-only scan (§B.5.2). |
| `dle_rate_limited` large | The generator is running from too few source addresses and has drained its own §E.9 404 budget. Spread the load, or raise `Dle:RateLimits:Edge:NotFound` for the run and say so in the report. |
| `dle_correct_outcome` below threshold with many 404s | The corpus does not match the instance. Re-run `seed.js`. |
| Spike `p(99)` over 200 ms | The autoscaler, not the code. §D.6 expects the tail to hold *during* scaling; if it does not, the HPA's reaction time and the pod's start-up cost are the subject, not the resolve path. |

## What this profile does not cover

`/v1/resolve` and `/v1/events` — the SDK ingestion endpoints — are not exercised here. Their limits
are one call per install lifetime and 60 events a minute per install (§E.9), so a load profile for
them is a different shape entirely: many identities making one request each, rather than few
identities making many. S-07 asks for rate limits to be tested on *all* public endpoints, so that
profile is still owed; it is not in §D.5 and is not invented here.
