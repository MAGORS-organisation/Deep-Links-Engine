# Deep Link Engine

**Self-hosted, EU-first deep linking and install attribution for mobile apps.** Branded short links that
open the right screen in your app, deferred deep linking through the app-store install, and attribution
that tells you honestly how sure it is. One `docker compose up`, your PostgreSQL, your data.

> Status: initial implementation on the `develop` branch. The .NET services, the web SDK and the admin
> console build and their tests pass. The Android and iOS SDKs are written but have not yet been compiled
> anywhere. Read [Where this stands](#where-this-stands) before relying on any of it.

## Why this exists

Firebase Dynamic Links was shut down on 25 August 2025. Google's suggested replacements are commercial
mobile measurement partners whose real-world contracts run to tens of thousands of dollars a year, and
whose attribution model rests on probabilistic device fingerprinting — a mechanism the final EDPB
Guidelines 2/2023 place under ePrivacy Article 5(3) consent requirements in the EU. For a European
company with an app, a compliance officer and a finance director, that combination is a problem.

This engine is the alternative for that company: it runs inside your own infrastructure, so the operator is
the data controller and no third party ever sees a click; it does deterministic attribution first and
treats probabilistic matching as an opt-in module that is off by default; and every attribution it
records carries a `match_type` and a `confidence`, because a probabilistic match presented as a certainty
is the industry's habit, not a law of nature.

It is not an attempt at feature parity with Branch or AppsFlyer, and it says so in
[docs/zadanie.md §F.3](docs/zadanie.md). The wedge is self-hosting, EU compliance and demonstrable
attribution.

## What it does

| Layer | What you get |
|---|---|
| **Link resolution** | Branded domains, 8-character slugs, routing rules by platform / OS version / country / language / channel / time window / A/B split, Open Graph previews for crawlers, QR codes. Never a `301`. |
| **Platform integration** | `apple-app-site-association` and `assetlinks.json` generated per domain, including Android 15+ `dynamic_app_link_components`; a verifier that catches the redirect and the Play App Signing fingerprint mistakes that break links silently. |
| **Deferred deep linking** | Android: deterministic via the Play Install Referrer. iOS: claim code, login reconciliation and direct-open reporting — because iOS has no referrer equivalent and nothing else is honest. |
| **Attribution** | `match_type` ∈ `install_referrer` · `login` · `claim_code` · `direct_open` · `probabilistic` · `none`, each with a confidence. Probabilistic is opt-in, consent-gated, and windowed to 60 minutes, not 7 days. |
| **Analytics** | Partitioned click stream in PostgreSQL, rollups, a dashboard that reports the deterministic / probabilistic / unmatched split, optional ClickHouse for large volumes. |
| **Privacy** | Three consent modes per tenant (`off`, `aggregate_only`, `full`); IP hashed with a daily-rotated salt or not stored at all; no third-party call on the resolve path, including GeoIP. |
| **Operations** | Signed webhooks (HMAC + Ed25519), rate limits with a separate budget for 404s so scanners cannot enumerate slugs, abuse reporting with quarantine rather than deletion, immutable audit log. |

## Quick start

Requirements: Docker with Compose, and a domain you can point at the box.

```bash
git clone https://github.com/MAGORS-organisation/Deep-Links-Engine.git
cd Deep-Links-Engine/deploy
cp .env.example .env            # set POSTGRES_PASSWORD and DLE_MASTER_SECRET at minimum
docker compose up -d
```

Then open the console at `https://<your-domain>/admin/` (or the API reference at `/scalar/v1`), create a
tenant and an API key, register a domain, register your app, and create a link. The first-30-minutes
walkthrough is in [docs/self-hosting/quickstart.md](docs/self-hosting/quickstart.md).

For Kubernetes there is a Helm chart under [deploy/helm/dle](deploy/helm/dle) with an HPA for the edge
tier and a NetworkPolicy that enforces the no-egress rule.

## How it is built

Two deployment units from one solution, one database (ADR-010):

```
                ┌──────────────┐   GET /{slug}, /.well-known/*          ┌────────────┐
  users, bots ─▶│    Caddy     │───────────────────────────────────────▶│  dle-edge  │──▶ Valkey (L2 cache)
                │  TLS, HTTP/3 │   /api/*, /v1/*, /admin/*               │  :8080     │──▶ PostgreSQL (one query)
                └──────┬───────┘───────────────────────────────────────▶└─────┬──────┘
                       │                                                      │ async batch (COPY)
  SDKs ────────────────┘                                                      ▼
                                                                        ┌────────────┐
                                                                        │ dle-control│──▶ PostgreSQL (EF Core)
                                                                        │  :8081     │──▶ webhooks, workers
                                                                        └────────────┘
```

- **Edge** is the hot path: a Dapper query behind a stampede-safe cache, client classification, rule
  evaluation, a 302 or an interstitial page with a real anchor element (in-app webviews only fire a
  Universal Link on a genuine tap). Telemetry goes into a bounded channel that drops under load rather
  than delaying the redirect.
- **Control** is EF Core: links, domains, apps, keys, the SDK endpoints, analytics, webhooks, abuse, and
  six leader-elected background workers.
- **Crypto** is agile from the first commit: every signed artefact carries `alg` and `kid`, and the webhook
  signature has a reserved slot for a post-quantum algorithm later without a breaking change.

The full reasoning is in [docs/adr](docs/adr) and [docs/architecture](docs/architecture). The Slovak
specification everything traces back to is [docs/zadanie.md](docs/zadanie.md).

## Where this stands

Be precise about what "done" means here.

| Component | Built | Verified how |
|---|---|---|
| `Dle.Domain`, `Dle.Crypto`, `Dle.Persistence*`, `Dle.Analytics.*` | ✅ | `dotnet build -warnaserror` 0/0; 1 430 unit tests pass; Feistel bijection proven exhaustively at narrow widths |
| `Dle.Edge`, `Dle.Control` | ✅ | Both hosts start; 75 contract + 850 security tests pass; 404/410/302 paths verified through `WebApplicationFactory` |
| Integration suite (103 tests, Testcontainers) | ✅ | **Green in CI** against PostgreSQL 18 and Valkey 8, including the chaos tests that stop PostgreSQL and point the edge at an unreachable Valkey. Its first run found seven defects and an adversarial review of the fixes found nine more; all are fixed and listed in the changelog |
| EF Core migration | ✅ | Applied and rolled back on a live PostgreSQL 18 by the integration suite: every table the product writes to exists, both event streams are partitioned and accept inserts, `ix_links_resolve` is answered by an index-only scan |
| Web SDK `@magors/dle-web` | ✅ | lint, typecheck, build, 145 tests, 8.85 kB gzip |
| Admin console | ✅ | lint, typecheck, Vite build, copied into the control host |
| Android SDK | ✅ | Compiled and unit-tested in CI (`sdk-android.yml`, JDK 17, Gradle 8.11) |
| iOS SDK | ✅ | Built and tested in CI on the iOS Simulator (`sdk-ios.yml`, Xcode, Swift 6) |
| Container images, compose stack, ZAP | ✅ | Both images build in CI, pass Trivy, and the compose stack starts for an OWASP ZAP baseline against the edge (`security.yml`) |
| Coverage | ⚠️ | Domain tier 95.5 % / 94.7 % (gate ≥ 90 %); overall **61.4 % against a 70 % target** — `Dle.Control` (39.2 %) and `Dle.Analytics.Postgres` (4.6 %) are the gap; the overall gate warns until it is reached |
| Load profile (k6, §D.5) | ✅ written | **Never run** — needs a deployed, seeded instance |
| 8-device manual matrix (§D.2) | — | Not automatable by design; pending |

Coverage: the domain tier (routing engine, classifier, attribution matcher, crypto) is above the 90 %
target; overall is 47 % against a 70 % target, with `Dle.Control` endpoints the main gap.

Two defects the test suite found and fixed are worth knowing about, because they are the kind that
never show up in a demo: under `InvariantGlobalization` the NFKC slug normalisation was a silent no-op,
so the homoglyph defence never ran; and key rotation could not retire the bootstrap key, so a leaked
key stayed valid forever while the operator saw a successful rotation. Both are in the commit history.

## What it deliberately does not do

From [docs/zadanie.md §A.6](docs/zadanie.md): it is not a mobile measurement partner (no SKAdNetwork or
AdAttributionKit aggregation, no ad-network connectors, no fraud scoring), it is not a CDN or edge runtime,
it does not send email or SMS, it does not support Android Instant Apps, and it does not ship its own
OAuth authorization server — it authenticates the console against your OIDC provider.

Fingerprinting is not a primary strategy. It is a module, it is off, and turning it on requires recorded
consent from the person being matched.

## Compared with the alternatives

| | Branch / AppsFlyer | Dub | Deep Link Engine |
|---|---|---|---|
| Hosting | Vendor SaaS, US | SaaS or self-host | Self-host only, anywhere |
| Data controller | Vendor is processor; DPA needed | Depends | You; no DPA needed |
| Deferred deep linking | Yes, probabilistic-heavy | No | Deterministic first; probabilistic opt-in |
| Attribution confidence exposed | Rarely | n/a | Always, per record |
| Stack | — | TypeScript | .NET 10, PostgreSQL |
| Licence | Commercial | AGPL-3.0 + commercial | MIT |

## SDKs

| Platform | Package | Notes |
|---|---|---|
| Android | [`sdk/android`](sdk/android) — `sk.magors.dle:dle-sdk` | Install Referrer, App Links, offline queue; no clipboard, no ad ID |
| iOS | [`sdk/ios`](sdk/ios) — Swift Package `DleSDK` | Universal Links, claim code, offline queue; privacy manifest ships with it |
| Web | [`sdk/web`](sdk/web) — `@magors/dle-web` | Smart banner, web fallback; zero dependencies, no fingerprinting |

## Documentation

- [docs/self-hosting](docs/self-hosting) — quick start, configuration, domains, upgrading, backup, troubleshooting
- [docs/integration](docs/integration) — HTTP API, the three SDKs, webhooks, migrating from Firebase Dynamic Links
- [docs/compliance](docs/compliance) — privacy model, DPIA and RoPA templates, regulatory map, security overview
- [docs/operations](docs/operations) — runbook, release checklist, device test matrix
- [docs/adr](docs/adr) — the thirteen architecture decisions, including the one where this repository departs from the specification
- [docs/dev/SHARED-KERNEL.md](docs/dev/SHARED-KERNEL.md) — the binding contract every project codes against

## Licence

MIT. The specification recommended AGPL-3.0 with a contributor licence agreement to protect against a
closed hosted fork; the project owner chose MIT for adoption and zero legal friction. That trade-off is
recorded, not hidden, in [ADR-0011](docs/adr/0011-license-mit.md).
