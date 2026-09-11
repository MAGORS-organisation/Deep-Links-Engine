# Changelog

All notable changes to Deep Link Engine are documented here. The format follows
[Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). Until `1.0.0`, minor versions may
contain breaking changes; they are called out as such.

Entries under *Unreleased* are written by the pull request that makes the change (§C.7 DoD 5).

## [Unreleased]

No release has been tagged. Everything below is the state of `develop`, and the *Not yet
verified* section is as important as the rest: it lists what exists in the repository but has
not been executed anywhere.

### Added

- **Edge (data plane)** — slug resolve with L1/L2 caching (HybridCache over Valkey), client
  classification, routing rules, 302 redirect or interstitial (CSP without `unsafe-inline`), Open
  Graph documents for verified crawlers (302-not-301, ADR-009), `/.well-known/apple-app-site-association`
  and `/.well-known/assetlinks.json`, QR codes, per-endpoint rate limiting with a separate 404
  budget against slug enumeration (§E.9), abuse reports, GeoIP from an operator-supplied MaxMind
  database. Serves from cache with PostgreSQL unavailable.
- **Control plane** — tenants, applications, domains with background AASA/assetlinks verification,
  links (single and bulk with idempotency keys), API keys and OIDC bearer authentication, RBAC,
  webhooks, OpenAPI with Scalar UI, `/healthz` and `/readyz` (the latter gated on the database).
- **Attribution** — `/v1/resolve`, install-referrer and claim-code matching, deferred deep links,
  consent modes (`aggregate_only` default) and IP storage policy (`hash_only` default).
- **Click stream** — partitioned `click_events` with retention (pg_partman when available, a plain
  SQL fallback otherwise), a PostgreSQL analytics sink and a ClickHouse sink.
- **Crypto** — master-secret derivation for every keyed primitive, slug permutation, HMAC-SHA-256
  and Ed25519 signatures with constant-time verification, key wrapping and rotation.
- **Admin console** (`src/Dle.Admin.Web`) — React 19 / TypeScript / Vite, no component library,
  no CDN; links, domains, dashboard, rules simulator. Built into `Dle.Control/wwwroot/admin`.
- **Web SDK** (`sdk/web`, `@magors/dle-web`) — smart app banner, web fallback, click-context
  resolve and event reporting; no fingerprinting, no clipboard reads, no third-party calls; size
  budget enforced.
- **Android SDK** (`sdk/android`, `sk.magors.dle`) — install referrer, App Links handling, event
  queue with backoff, sample app.
- **iOS SDK** (`sdk/ios`, `DleSDK`) — Universal Links, deferred resolve, event queue, privacy
  manifest, sample app; dependency-free (URLSession only).
- **Tests** — `Dle.UnitTests` (1 419), `Dle.ContractTests` (75), `Dle.SecurityTests` (850,
  including a property-based driver for the three §D.4 fuzz targets), `Dle.IntegrationTests` (97,
  Testcontainers postgres:18 + valkey:8, two-tenant isolation and chaos cases).
- **Deployment** — Profile A compose stack (Caddy → edge ×2 + control → postgres:18 + valkey:8)
  with hardened service defaults, Dockerfiles for edge and control (multi-arch, chiseled runtime
  variant, EF Core migration bundle in the control image), a pg_partman-enabled Postgres image,
  Postgres tuning and init scripts, and a Profile B Helm chart. Aspire AppHost for local
  development.
- **Build** — .NET 10 solution with central package management and per-project lock files
  (`--locked-mode` restore), `TreatWarningsAsErrors` with `latest-Recommended` analyzers.
- **CI/CD** (`.github/`) — CI (build, format, four suites, coverage gate: domain tier ≥ 90 % hard,
  overall 70 % warning), admin UI, web SDK, Android SDK and iOS SDK workflows, Security (NuGet /
  npm / Trivy fs and image scans, licence policy, OWASP ZAP baseline against the compose stack),
  SBOM + CBOM with schema validation, CodeQL (C#, JavaScript/TypeScript), Release (multi-arch images
  to GHCR with SLSA provenance and cosign keyless signatures, signed Helm chart, compose smoke, ZAP
  and k6 gates, SBOM/CBOM attached), Nightly (domain re-verification, CVE feed recheck, extended
  fuzz-harness run), Dependabot for every ecosystem, issue and PR templates, CODEOWNERS. Helper
  scripts under `.github/scripts/` for the TRX summary, the coverage gate, the NuGet vulnerability
  gate, the CBOM, CycloneDX validation and domain verification.
- **Project files** — `SECURITY.md` (private reporting, 90-day disclosure, scope, safe harbour,
  DSA Art. 16 abuse path), `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md` (Contributor Covenant 2.1),
  this changelog, root `.dockerignore` (identical to `deploy/docker/.dockerignore`).

### Fixed

- Four defects found by the test suites while they were being written (see commit
  `8f99c56`): recorded here so the first release notes do not present them as never having existed.

### Not yet verified — read before relying on anything above

- **Android SDK and iOS SDK have never been compiled.** They were written without a JDK/Android
  SDK and without Xcode. The `sdk-android.yml` and `sdk-ios.yml` workflows are their first build;
  a red first run is expected and is not a CI defect.
- **Container images and the compose stack have never been built or started** on the authoring
  machine (no Docker there). `security.yml` (image scan, ZAP) and `release.yml` are their first
  build and first start.
- **The Helm chart has never been rendered, linted or installed** (no Helm on the authoring
  machine). Its bitnami sub-chart version ranges are unresolved; `release.yml` runs
  `helm dependency update` and will fail loudly if they do not resolve.
- **`Dle.IntegrationTests` has not run in CI.** Locally it runs only with Docker present; CI sets
  `DLE_TESTS_REQUIRE_DOCKER=1` so a missing Docker fails instead of skipping.
- **The k6 load profile (`tests/load`) has never been run.** It needs a deployed, seeded instance
  and a control-plane API key; the release workflow runs it only when a staging target is
  configured and otherwise marks the release as pre-release.
- **OWASP ZAP baseline has never been run** against the edge; `.github/zap/edge-baseline.conf`
  silences four informational rules and nothing header- or CSP-related, so the first run will
  probably need review.
- **SharpFuzz is not wired up.** The fuzz targets exist and are driven by a property-based test;
  coverage-guided fuzzing needs a driver project and instrumentation that do not exist yet.
- **The `dotnet format --verify-no-changes` gate, the coverage gate and the licence policy** run
  for the first time in CI; the licence allow-list may need additions for packages whose metadata
  carries a licence URL rather than an SPDX expression.
- **Overall line coverage is ~47 % against a 70 % target**; the domain tier (`Dle.Domain`,
  `Dle.Crypto`) is above 90 %. The overall gate is a warning until the number is reached.
- **No external penetration test (S-10) and no staging key-rotation exercise (S-12)** have taken
  place; both are release-blocking for 1.0.

### Security

- Dependencies are locked and restored in locked mode; GitHub Actions are pinned to commit SHAs;
  releases are signed keyless with Sigstore and ship SBOM, CBOM and SLSA provenance (T-14, S-06).

[Unreleased]: https://github.com/MAGORS-organisation/Deep-Links-Engine/compare/master...develop
