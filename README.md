# Deep Link Engine

**Self-hosted, EU-first deep linking and install attribution for mobile apps.** Branded short links that
open the right screen in your app, deferred deep linking through the app-store install, and attribution
that tells you honestly how sure it is. One `docker compose up`, your PostgreSQL, your data.

> Status: pre-release, on the `develop` branch. Nothing is tagged, no image or SDK is published. The
> .NET services, the web SDK, the admin console and both mobile SDKs build and pass their tests in CI,
> against PostgreSQL 18 and Valkey 8 for the integration suite — but **the product's core flows do not
> work end to end yet**: an installed app opened by a link is not told which screen to open, deferred
> deep linking is off by default on Android and has no working path on iOS, and a fresh install has no
> way to obtain its first credential. Nothing has been tried on a real device, the k6 load profile has
> never run, and there has been no external penetration test. Read [Known gaps](#known-gaps) before
> relying on any of it.

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
| **Platform integration** | `apple-app-site-association` and `assetlinks.json` generated per domain; a verifier that catches redirects on `/.well-known` and association files that do not match the registered apps. (Android 15+ `dynamic_app_link_components` and a working Play App Signing warning are designed but not emitted yet — see [Known gaps](#known-gaps).) |
| **Deferred deep linking** | Designed as: Android deterministic via the Play Install Referrer; iOS claim code, login reconciliation and direct-open reporting — because iOS has no referrer equivalent and nothing else is honest. Today Android works only in `full` consent mode with a consent signal on the link, and the iOS paths are not wired — see [Known gaps](#known-gaps). |
| **Attribution** | `match_type` ∈ `install_referrer` · `login` · `claim_code` · `direct_open` · `probabilistic` · `none`, each with a confidence. Probabilistic is opt-in, consent-gated, and windowed to 60 minutes, not 7 days. |
| **Analytics** | Partitioned click stream in PostgreSQL, rollups, a dashboard that reports the deterministic / probabilistic / unmatched split, optional ClickHouse for large volumes. |
| **Privacy** | Three consent modes per tenant (`off`, `aggregate_only`, `full`); IP hashed with a daily-rotated salt or not stored at all; no third-party call on the resolve path, including GeoIP. |
| **Operations** | Signed webhooks (HMAC + Ed25519), rate limits with a separate budget for 404s so scanners cannot enumerate slugs, abuse reporting with quarantine rather than deletion, immutable audit log. |

## Quick start

Requirements: Docker with Compose, and a domain you can point at the box.

```bash
git clone --branch develop https://github.com/MAGORS-organisation/Deep-Links-Engine.git
cd Deep-Links-Engine/deploy
cp .env.example .env            # set POSTGRES_PASSWORD and DLE_MASTER_SECRET at minimum
docker compose -f docker-compose.yml up -d --build
```

Name the file explicitly: a plain `docker compose up` in `deploy/` also loads
`docker-compose.override.yml`, the development profile (Caddy's internal CA, `Development` mode,
tenant self-service on). No images are published yet, so `--build` builds them from source.

The walkthrough then has you create a tenant and an API key, register a domain and your app, and create
a link. **The credential step does not work on a fresh install today** — there is no bootstrap key and
the OIDC path is not wired (see [Known gaps](#known-gaps)). The first-30-minutes walkthrough in
[docs/self-hosting/quickstart.md](docs/self-hosting/quickstart.md) says what does.

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
- **Crypto** is designed to be agile: the webhook signature carries a key id and has a reserved slot for
  a post-quantum algorithm later without a breaking change. Not everything follows the design yet: click
  ids are a bare truncated HMAC without `alg`/`kid`, and key rotation is not implemented (see
  [Known gaps](#known-gaps)).

The full reasoning is in [docs/adr](docs/adr) and [docs/architecture](docs/architecture). The Slovak
specification everything traces back to is [docs/zadanie.md](docs/zadanie.md).

## Where this stands

Be precise about what "done" means here. Green CI proves that the components agree with each other; it
does not prove that a link opens the right screen on a phone. Every gap below sits at a hand-off
between components that no test crosses and no device has exercised.

### What is verified

| Component | Verified how |
|---|---|
| `Dle.Domain`, `Dle.Crypto`, `Dle.Persistence*`, `Dle.Analytics.Postgres` | `dotnet build -warnaserror` clean; unit suite green in CI; the slug permutation is proven a bijection exhaustively at narrow widths |
| `Dle.Edge`, `Dle.Control` | Both hosts start; contract and security suites green in CI; 404/410/302 paths verified through `WebApplicationFactory` |
| Integration suite (Testcontainers) | **Green in CI** against PostgreSQL 18 and Valkey 8, including the chaos tests that stop PostgreSQL and point the edge at an unreachable Valkey. The defects its runs and the review rounds found are fixed and listed in the changelog |
| EF Core migration | Applied and rolled back on a live PostgreSQL 18 by the integration suite: every table the product writes to exists, both event streams are partitioned and accept inserts, `ix_links_resolve` is answered by an index-only scan |
| Helm chart | Rendered, linted and schema-validated for three profiles, and installed into a kind cluster in CI (migration Job, tables, both services answering, in-place upgrade) |
| Web SDK, admin console | lint, typecheck, build and unit tests in CI |
| Android / iOS SDK | Compiled and unit-tested in CI (JDK 17 / Gradle 8.11; Xcode on the iOS Simulator) |
| Container images, ZAP | Both images build and pass Trivy. The ZAP baseline runs against the development compose profile with an empty database, straight at the edge: it scans the 404 page and `robots.txt`, not Caddy, TLS, redirects, interstitials or `/.well-known` |
| Coverage | Domain tier above the 90 % gate, overall above the 70 % gate (`--overall-mode fail`); the exact figures are in the CI summary. `Dle.Analytics.ClickHouse` has no tests and does not appear in the coverage figures |

Not verified at all: **any real device** (the 8-row matrix of §D.2), the k6 load profile (§D.5 — it
cannot pass as written, see [tests/load](tests/load)), an install by someone who did not write it
(§D.7 item 6), an external penetration test, and the release pipeline (`release.yml` has never run).

### Known gaps

Confirmed against the code on 2026-09-24. Each one contradicts something the specification or an
earlier version of these documents promised.

**Core flows**

- **Direct open has no link context.** An installed app opened by a Universal Link or App Link receives
  only the short URL (`https://go.example.com/aB3xK9pQ`). The SDK plane has `POST /v1/resolve`,
  `/v1/events` and `/v1/claim-codes` only; nothing turns a slug into its `deeplink_path` and params, so
  the app cannot open the target screen unless it parses human-readable slugs itself.
- **Android deferred deep linking is off by default.** `dl_cid` goes into the Play referrer only when the
  tenant's `consent_mode` is `full` **and** the click carries attribution consent — and the only
  click-time signal the edge reads is `dl_consent=all` (or `gdpr=0`) on the short URL, i.e. consent
  asserted by whoever built the link. Under the default `aggregate_only` mode `/v1/resolve` answers
  `none` with `consent_missing`. Whether deep-link context may be delivered without consent is the
  open legal question Q4 of [§F.2](docs/zadanie.md).
- **iOS deferred deep linking has no working deterministic path.** The edge never issues or shows a
  claim code, and login matching reads a click field nothing writes.
- **The interstitial's "open in app" button** is a custom-scheme URL, never a Universal Link or App Link,
  or it is missing when the app has no custom scheme.
- **A link without `routing_rules` is web-only**: always a 302 to `target_url`, never the store and never
  an interstitial, in-app browsers included. Store and interstitial behaviour needs an explicit
  `app_or_store` or `store_only` rule with its own `store_url`.
- **Deferred resolve returns the link-level `deeplink_path`**, not the one of the rule that matched.

**Operating it**

- **No working first credential.** The OIDC tenant claim (`dle_tenant`) is never mapped to the claim the
  control plane reads, the admin console has no OIDC sign-in (it takes a pasted API key), and there is
  no bootstrap key.
- **Multi-tenant isolation defect.** A request carrying an SDK key of tenant A *and* an API key of tenant
  B is scoped to A and authorised as B. Do not host mutually untrusted tenants on one instance. There
  is no PostgreSQL row-level security; isolation is the EF Core query filter plus explicit `tenant_id`
  predicates.
- **The public abuse form** (`POST /abuse-reports`, DSA Art. 16) is unreachable: Caddy and the Helm
  ingress route that path to the edge, which does not serve it.
- **Quarantine does not invalidate the edge cache**: a quarantined link keeps redirecting for up to
  10 min 30 s with the default cache settings.
- **URL reputation checking is off by default**, and with URLhaus enabled but no Auth-Key every lookup
  fails and the target is treated as safe.
- **The ClickHouse provider is not wired end to end** and switches PostgreSQL retention off. Do not enable
  it.
- **Profile A** serves TLS for one host only, ships no alert rules or dashboards (metrics leave only via
  OTLP), and has no WAL archiving.
- **The nightly workflow, scheduled CodeQL and Dependabot have never run**: GitHub reads them from the
  default branch, which is still `master` with only the initial commit.

**Security and privacy claims not yet backed by code**

- **Key rotation is not implemented**, and the internet-facing edge receives the master secret from which
  the control plane's webhook signing key also derives.
- **IP hashes are not unlinkable for the operator**: the daily salt is derived from the master secret,
  so every past salt can be recomputed.
- **The Android and iOS SDKs send `install_id` before any consent**, and the server stores install and
  event rows in every consent mode.
- **There is no erasure or access endpoint** by `install_id` or IP hash; data-subject requests are SQL
  work for now.

**Distribution**

- **Nothing is published**: not the images, not `@magors/dle-web` on npm, not the Android SDK; the iOS
  package cannot be added by URL (its `Package.swift` is not at the repository root and there are no
  tags). The web SDK cannot call `dle-control` cross-origin, because the control plane has no CORS
  support.

Two defects the test suite found and fixed are worth knowing about, because they are the kind that
never show up in a demo: under `InvariantGlobalization` the NFKC slug normalisation was a silent no-op,
so the homoglyph defence never ran; and key rotation could not retire the bootstrap key, so a leaked
key stayed valid forever while the operator saw a successful rotation. Both are in the commit history.

## What it deliberately does not do

From [docs/zadanie.md §A.6](docs/zadanie.md): it is not a mobile measurement partner (no SKAdNetwork or
AdAttributionKit aggregation, no ad-network connectors, no fraud scoring), it is not a CDN or edge runtime,
it does not send email or SMS, it does not support Android Instant Apps, and it does not ship its own
OAuth authorization server — the console is meant to authenticate against your OIDC provider (not wired
yet, see [Known gaps](#known-gaps)).

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
| iOS | [`sdk/ios`](sdk/ios) — Swift Package `DleSDK` | Universal Links, claim code (the server does not issue codes yet), offline queue; privacy manifest ships with it |
| Web | [`sdk/web`](sdk/web) — `@magors/dle-web` | Smart banner, web fallback; zero dependencies, no fingerprinting |

None of the three is published yet; build them from source (see [Known gaps](#known-gaps)).

## Documentation

- [docs/self-hosting](docs/self-hosting) — quick start, configuration, domains, upgrading, backup, troubleshooting
- [docs/integration](docs/integration) — HTTP API, the three SDKs, webhooks, migrating from Firebase Dynamic Links
- [docs/compliance](docs/compliance) — privacy model, DPIA and RoPA templates, regulatory map, security overview
- [docs/operations](docs/operations) — runbook, release checklist, device test matrix
- [docs/adr](docs/adr) — the thirteen architecture decisions; the index lists where this repository departs from the specification
- [docs/dev/SHARED-KERNEL.md](docs/dev/SHARED-KERNEL.md) — the binding contract every project codes against

## Licence

MIT. The specification recommended AGPL-3.0 with a contributor licence agreement to protect against a
closed hosted fork; the project owner chose MIT for adoption and zero legal friction. That trade-off is
recorded, not hidden, in [ADR-0011](docs/adr/0011-license-mit.md).
