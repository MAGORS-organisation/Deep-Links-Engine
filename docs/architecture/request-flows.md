# Request flows

**What this is:** the four sequences of [§B.6](../zadanie.md#b6-tok-requestu) — a click, an Android install, an iOS install, and a direct open — with the latency budget and the one partition-pruning detail that decides whether attribution stays fast after six months.
**Who it is for:** engineers touching the edge pipeline or the attribution service, and SDK integrators who want to know what the server expects of them.

## 1. Click resolution ([§B.6.1](../zadanie.md#b61-rozlíšenie-kliku))

```mermaid
sequenceDiagram
    participant U as Client
    participant E as Edge Resolver
    participant H as HybridCache (L1/L2)
    participant P as PostgreSQL
    participant Q as Event channel

    U->>E: GET https://link.example.sk/aB3xK9pQ
    E->>E: 1. Normalise host + slug
    E->>H: 2. GetOrCreateAsync("lnk:{host}:{slug}")
    alt cache miss
        H->>P: SELECT … FROM links WHERE domain_id=$1 AND slug=$2
        P-->>H: row
    end
    H-->>E: LinkSnapshot
    E->>E: 3. Classify client (bot? webview? platform? geo?)
    alt crawler
        E-->>U: 200 HTML (OG tags) — done
    end
    E->>E: 4. Consent gate (tenant + domain + region)
    E->>E: 5. Evaluate routing_rules → Decision
    E->>E: 6. Generate click_id (Feistel over timestamp_ms || sequence → base62)
    E->>Q: 7. TryWrite(ClickEvent)  [non-blocking, bounded channel]
    alt webview / interstitial
        E-->>U: 200 interstitial HTML (+ <a> to the deep link, + JS fallback timeout)
    else
        E-->>U: 302 Location: <store URL with referrer> or <web fallback>
    end
```

Step 2 is one query on a covering index ([data-model.md](data-model.md#the-covering-index)); step 3 uses UA parsing plus reverse DNS for crawler verification and GeoIP from a memory-mapped MaxMind file — **no third-party call on the hot path** ([NFR-14](../zadanie.md#a5-nefunkčné-požiadavky)); step 5 is the rule engine of [routing-rules.md](routing-rules.md); step 7 never blocks — under load the event is dropped and counted in `dle_click_events_dropped_total` ([observability.md](observability.md)) rather than delaying the response. The response shape per client class is [ADR-0009](../adr/0009-http-response-shape-never-301.md).

### Latency budget (cache hit)

| Step | Budget |
|---|---|
| Normalisation | 0.2 ms |
| Cache lookup (L1, or L2 on a cold replica) | 0.5 ms |
| Classification (UA parsing + GeoIP from the MMAP file) | 1.5 ms |
| Rule evaluation | 0.3 ms |
| Response generation | 1.0 ms |
| Kestrel overhead | ~2 ms |
| **Total** | **~5.5 ms p50**, headroom to the 8 ms target of [NFR-01](../zadanie.md#a5-nefunkčné-požiadavky) |

A cache miss adds one PostgreSQL round trip and is budgeted separately: p99 ≤ 120 ms ([NFR-02](../zadanie.md#a5-nefunkčné-požiadavky)). The budget has been designed and unit-tested per step; it has **not** been measured end-to-end under load on the build machine — the k6 profile in [performance.md](performance.md) has not run.

## 2. Deferred deep link — Android, deterministic ([§B.6.2](../zadanie.md#b62-deferred-deep-link--android-deterministický))

```mermaid
sequenceDiagram
    participant U as User
    participant E as Edge Resolver
    participant G as Google Play
    participant A as App + SDK
    participant S as Attribution Service
    participant P as PostgreSQL

    U->>E: click on the link
    E->>P: write ClickEvent(click_id=C1)
    E-->>U: 302 play.google.com/…&referrer=dl_cid%3DC1%26utm_source%3D…
    U->>G: install
    G->>A: first launch
    A->>A: InstallReferrerClient.getInstallReferrer()
    A->>S: POST /v1/resolve { install_id, referrer, platform }
    S->>P: SELECT … WHERE click_id='C1' AND occurred_at BETWEEN t0 AND t1
    S->>P: INSERT installs; INSERT attributions(match_type='install_referrer', confidence=1.00)
    S-->>A: { deeplink_path:"/promo/autumn", campaign:…, match_type:"install_referrer", confidence:1.0 }
    A->>A: navigate to the target screen
```

The referrer string is built from the rule's `referrer_template` (default `dl_cid={click_id}&utm_source={utm_source}&utm_medium={utm_medium}&utm_campaign={utm_campaign}`, capped at 500 encoded characters — [routing-rules.md](routing-rules.md#store-urls-and-the-install-referrer)). This is strategy S1 of [ADR-0008](../adr/0008-deferred-deep-linking-strategies.md): deterministic, confidence 1.00, no friction.

## 3. Deferred deep link — iOS, no determinism from the platform ([§B.6.3](../zadanie.md#b63-deferred-deep-link--ios-bez-determinizmu-z-platformy))

```mermaid
sequenceDiagram
    participant U as User
    participant E as Edge Resolver
    participant AS as App Store
    participant A as App + SDK
    participant S as Attribution Service

    U->>E: click on the link
    E->>E: write ClickEvent(click_id=C1) + park the context in Valkey (TTL 1 h)
    E-->>U: 200 interstitial: [Open in the app] + (optionally) code "K7QP2M"
    U->>AS: redirect to the App Store (?pt=&ct=&mt=8)
    U->>A: install and first launch
    A->>S: POST /v1/resolve { install_id, platform, signals?, claim_code? }
    alt claim_code supplied
        S-->>A: context, match_type="claim_code", confidence=1.00
    else user signs in
        S-->>A: context, match_type="login", confidence=1.00
    else probabilistic module enabled + consent recorded
        S->>S: match within ≤ 60 min on IP prefix + OS + language + time
        S-->>A: context, match_type="probabilistic", confidence=0.3–0.9
    else
        S-->>A: { match_type:"none" } → the app continues with ordinary onboarding
    end
```

This is the most important design compromise in the product: nothing is promised on iOS that cannot be kept. Three deterministic paths (S3 claim code, S2 login, and S0 direct open below) and one honestly labelled probabilistic one, off by default.

### Partition pruning: `click_id` embeds its timestamp

`click_events` is range-partitioned by `occurred_at` ([data-model.md](data-model.md#the-partitioned-click-stream)). A query `WHERE click_id = 'C1'` **with no time predicate cannot prune partitions**; with 180 days of retention it searches up to 180 indexes, and the cost grows linearly with the age of the installation — it will not show in a demo and will show after half a year of production. Therefore the `click_id` carries an **encrypted timestamp**: the same keyed Feistel construction as the slug ([ADR-0007](../adr/0007-slug-generation-keyed-feistel-base62.md)) applied to `timestamp_ms || sequence`. The attribution service decrypts it and queries `occurred_at BETWEEN t0 − 5 min AND t0 + 5 min`, which touches one or two partitions. Without this the design carries a silent performance debt; with it, the lookup is bounded regardless of retention.

## 4. Direct open — the most common case, and the one that gets forgotten ([§B.6.4](../zadanie.md#b64-priame-otvorenie-najčastejší-prípad-ktorý-sa-zabúda))

```mermaid
sequenceDiagram
    participant U as User
    participant OS as iOS / Android
    participant A as App + SDK
    participant S as Attribution Service

    U->>OS: taps https://link.example.sk/aB3xK9pQ
    OS->>OS: domain verified via AASA / assetlinks → app is installed
    OS->>A: opens the app directly — no HTTP request reaches the edge
    A->>A: continueUserActivity / onNewIntent
    A->>S: POST /v1/events { type:"link_open", url, install_id }
    S-->>A: 202
```

When the app is installed the OS opens it **without any request to the edge**. The SDK therefore **must** report the open. Without that call: the click is never counted, re-engagement campaigns are unmeasurable, and statistics are systematically under-reported for exactly the most successful campaigns. This is strategy S0 — fully deterministic, `match_type: "direct_open"`.

## Where the flows are verified

| Flow | Verified on the build machine | Not yet |
|---|---|---|
| 1 Click resolution | 404 / 410 / 302 paths through `WebApplicationFactory` (security suite); classifier and rule engine in the domain tier (≥ 90 % coverage) | latency under load (k6 never run); real-device behaviour (8-device matrix pending) |
| 2 Android deferred | `/v1/resolve` contract and security tests; matcher unit tests | the Android SDK has never been compiled; end-to-end through Play is manual |
| 3 iOS deferred | as above; claim-code and login paths unit-tested | the iOS SDK has never been compiled; Valkey context parking is in the integration suite (needs Docker) |
| 4 Direct open | `/v1/events` contract tests | SDK reporting on real devices |
