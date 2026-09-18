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
- **Tests** — `Dle.UnitTests`, `Dle.ContractTests`, `Dle.SecurityTests` (including a
  property-based driver for the three §D.4 fuzz targets) and `Dle.IntegrationTests`
  (Testcontainers postgres:18 + valkey:8, two-tenant isolation and chaos cases). The counts are in
  *Verified in CI on this pull request* below, which is the one place they are kept current.
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

- **Control plane** — `Dle:Webhooks:AllowPrivateDestinations` could not do the one thing it exists
  for. The switch was read after the address rules, and those refuse every loopback and private
  literal, so an operator who turned it on still could not point a subscription at a listener on
  their own machine or an internal network. It is read before them now, and it also allows plain
  `http`, because a loopback listener with a certificate nobody trusts is not something a
  development machine or a test can usefully arrange. Everything the switch turns off is the SSRF
  defence of T-02, and a security test pins both directions.
- **Tests** — assertions that could not fail, and two claims the code does not keep. A revoked key
  was only ever presented for the first time after its revocation, so the credential cache was
  never in the path and the property under test was not the one documented: revocation takes
  effect within `Dle:Identity:CredentialCacheSeconds`, which the configuration reference has always
  said and two code comments denied. There is now a test that uses a key, revokes it and presents
  it again with the cache out of the way. A link delete asserted that no revisions survived a link
  that never had any; it creates and edits the link first. The link write policy pinned viewer and
  owner but not editor, which is where the line actually is. The bot assertion could not fail, and
  the rank guard named in a test's name is unreachable over HTTP.
- **Tests** — the `Spec` traits named requirements that denote something else in `docs/zadanie.md`
  (`FR-244` is URL reputation, not tenant deletion) or nothing at all (`FR-3xx`), so a report
  grouped by requirement credited the wrong ones and showed the real ones as uncovered. They are
  re-mapped, and a contract test now fails on any `Spec` trait the specification does not define.
- **Tests** — webhook signing had no test of a delivery that leaves the host. The algorithm has
  unit, contract and security tests, but every delivery test pointed at a public address that
  refuses the request, so nothing checked that a subscriber receives a header they can verify. The
  suite now listens on loopback, reads the request that arrived, and verifies its signature against
  the secret the API handed out — and re-verifies it over a changed body to prove the check bites.
- **Control plane** — `PATCH /api/v1/links/{id}` with only a new `target_url` left the stored
  routing rules pointing at the old one. A link created without rules is stored with a catch-all
  synthesised from its target, and the edge routes from the rules alone, so every visitor kept
  going to the old destination while the API, the revision history and the console all reported
  the new one. A catch-all web rule that was pointing at the target now moves with it; a rule set
  the caller authored is left exactly as written.
- **Analytics** — the rollup job marked spans it had never aggregated as covered. A pass
  aggregates at most `RollupMaxWindowHours`, but the watermark was advanced to the present
  regardless, so on a database with events older than one window (a fresh install, or a job that
  had been down) every report over the skipped span was answered from the empty rollup tables as
  zeros instead of from the raw events. Each pass now starts where the last one ended, so the
  covered span stays contiguous, and `analytics_rollup_state` records the lower bound as well:
  a report is answered from a rollup only when its window lies inside what that rollup has really
  aggregated. This became reachable in the previous release, when the first rollup pass on an
  empty state table stopped throwing.
- **Analytics** — retention could delete raw events the rollups had never read. The partition drop
  was the first thing a run did, before every statement that needs the analytics schema, so on an
  instance where that schema is missing each run destroyed partitions older than `RawDays` and
  then failed before writing its audit row (§E.6.3, FR-247). The run now reads the rollup
  watermark first, which fails before anything is dropped when the schema is absent, and the raw
  cutoff never passes that watermark: a rollup job that is behind holds the drop back and says so
  (event 6306).
- **Analytics** — three defects the analytics integration tests found on their first run against
  a live PostgreSQL, all fixed in the same pull request:
  - The rollup job crashed on a database that had never been aggregated: with no state row the
    earliest covered instant is `DateTimeOffset.MinValue`, and stepping the overlap back from it
    threw before the first query. The floor is applied first now.
  - The retention job never dropped a partition: the partition listing returned `partition_name`
    and `upper_bound`, which Dapper does not map onto `PartitionName` and `UpperBound`, so every
    partition looked unbounded and was skipped. The columns are aliased to the property names.
  - The Parquet export died mid-stream with a 500: Parquet.Net flushes synchronously and Kestrel
    refuses synchronous writes on the response body. The file is assembled in memory and copied
    out asynchronously.
- **Analytics** — the rollup schema (`click_rollup_*`, `install_rollup_*`,
  `attribution_quality_daily`, `analytics_rollup_state`, `analytics_retention_runs` and the
  `dle_platform_of` function) shipped as an embedded script that nothing ever ran: on a fresh
  database the rollup and retention workers failed on every run and a breakdown by platform
  was a 500. The control plane now applies the idempotent script when it starts
  (`PostgresAnalyticsSchema`); a failure is logged and leaves link management untouched.
- **Control plane** — `POST /api/v1/webhooks` without `is_active` registered an inactive
  subscription that received nothing: the request is read through a source-generated JSON
  context, which materialises an init-only record through an object initializer and gives an
  absent member `default(bool)` rather than the declared `true`. The member is now nullable and
  an omitted value means active.
- **Control plane** — `DELETE /api/v1/domains/{id}` on a host that still serves links answered
  500 instead of `409 domain-in-use`: PostgreSQL reports an `ON DELETE RESTRICT` constraint as
  SQLSTATE `23001`, and only `23503` was recognised. The problem type itself was built inline and
  so appeared in neither `ProblemCodes.All` nor the published OpenAPI catalogue; it is declared and
  documented now, which is what makes it something an integrator can branch on.
- **Control plane** — `POST /api/v1/apps` for a platform and bundle identifier already registered
  in the tenant answered 500 (the unique violation escaped as a `DbUpdateException`); it now answers
  `409 app-taken`, a new problem type in the catalogue.
- **Control plane** — a row of a `POST /api/v1/links/bulk` batch that carries no link definition now
  echoes the caller's `ref` on its result line, so the failing row can be identified in the answer.
- **Control plane** — a `target_url` with a forbidden scheme (`javascript:`, `data:`) or that is
  not an absolute URL is refused as `422 unsafe-target` keyed on `target_url` (§E.3 step 1,
  TC-161). Before, the default rule synthesised from it failed as `400 invalid-routing-rules`
  on `routing_rules[0].then.url`, a field the caller never sent.
- Four defects found by the test suites while they were being written (see commit
  `8f99c56`): recorded here so the first release notes do not present them as never having existed.
- Seven defects found by the first CI run of the integration suite against a live PostgreSQL 18
  and Valkey 8, all fixed in the same pull request:
  - `sdk_events` did not exist. The SDK event batch writer copied into it and the funnel queries
    joined it, so every `POST /v1/events` was a 500. The `InitialSchema` migration now creates it,
    partitioned like `click_events`, and the plain-SQL partition fallback maintains both streams
    (`dle_click_events_maintain()`, `dle_sdk_events_maintain()`).
  - Every `Idempotency-Key` reservation was filed under a freshly minted, non-existent tenant and
    rejected by the cross-tenant guard: the identity stamp treated the `tenant_id` half of the
    composite key as an identifier to generate.
  - Updating a link that the same unit of work had already loaded failed on a duplicate tracked
    instance.
  - The control plane answered 500, not 503, while PostgreSQL was unreachable (§D.6).
  - The edge's readiness probe did not mention PostgreSQL at all; it now reports the database as
    degraded (still 200, so cached links keep serving) while it is unreachable.
  - Nothing reported the shared cache as down while Valkey was unreachable (§D.6,
    `cache_l2_down`); a probe now feeds the `dle.cache.l2.up` gauge.
  - `ix_links_resolve` did not cover five columns the edge's resolve statement reads, so the
    resolve was never an index-only scan (§B.5.2).

- Nine further defects found by an adversarial review of those fixes (two reviewers per
  finding), all fixed in the same pull request:
  - A link's `version` is now its optimistic-concurrency token. A PATCH prepared from a read that
    a concurrent edit or an abuse quarantine overtook was written in full and silently reversed
    the newer write - a takedown could be undone by an unrelated edit. Such a write now answers
    `409 write-conflict`; quarantine and release bump the version and appear in the link's
    history, and enforcement retries on a conflict so it always wins.
  - A link created with `is_active: false` was inserted active: EF Core treats the CLR default
    as "not provided" for a column with a store default and omitted it. The four store-default
    booleans now carry a sentinel of `true`.
  - The plain-SQL partition fallback wedged permanently once a day's rows had landed in the
    default partition, and nothing in the product called it. `dle_partitions_maintain()` now
    adopts stranded days (detach the default, create the day, move the rows, attach again), and
    the control plane's retention job calls it on every run, keeping seven days of partitions
    ready for both event streams.
  - A `none` answer given for want of attribution consent was final (`expires_in: 0`), so both
    SDKs cached it for the life of the installation and a click could never be credited once
    consent arrived. It now carries the install-referrer window as `expires_in`, and both SDKs
    drop a cached unmatched answer when attribution consent is recorded.
  - A link whose resolve-time fields exceed the btree index-row limit of `ix_links_resolve`
    (about 2.7 kB after compression) answered 500; it now answers `422 link-too-large`.
  - The edge readiness probe checked the primary pool while resolves read the replica pool
    when one is configured; it now probes the pool the resolve statement uses.
  - The shared-cache probe could outlive its five-second budget on a connect attempt the client
    does not cancel; the budget is now the probe's own deadline.
  - A `RAISE WARNING` in the migration used a `format()` directive `RAISE` does not understand
    and printed a wrong remedy.
  - Two integration assertions were loosened by the earlier fixes: the 404 indistinguishability
    check now also compares lengths, and the covering-index check requires an index-only scan on
    `ix_links_resolve` itself with zero heap fetches.

### Verified in CI on this pull request

- 1 430 unit, 75 contract, 850 security and 219 integration tests pass; the integration suite runs
  against PostgreSQL 18 and Valkey 8 in Testcontainers, applies and rolls back the migration, and
  covers the chaos scenarios of §D.6 (PostgreSQL stopped, Valkey unreachable).
- The Android SDK compiles and passes its unit tests (JDK 17, Gradle 8.11); the iOS SDK builds and
  passes its tests on the iOS Simulator (Swift 6).
- Both container images build, pass Trivy, and the compose stack starts for an OWASP ZAP baseline
  against the edge. CodeQL (C#, JavaScript/TypeScript), the SBOM/CBOM, the NuGet vulnerability
  gate, the licence policy (four packages carry a licence override to MIT, verified upstream) and
  the format gate are green.
- Overall line coverage is 83.8 % against a 70 % target; the domain tier is at 97.4 %
  (`Dle.Domain`) and 94.7 % (`Dle.Crypto`) against a 90 % gate. The HTTP integration suite
  of the control plane took `Dle.Control` from 39.2 % to 78.7 % and
  `Dle.Analytics.Postgres` from 4.6 % to 79.9 %.

### Not yet verified — read before relying on anything above

- **The Helm chart has been linted, rendered and schema-validated in CI** (the `Helm chart`
  workflow: three profiles, kubeconform against Kubernetes 1.31) but **never installed on a
  cluster**. The bitnami sub-charts are pinned to the versions the registry served on first
  resolution (postgresql 18.11.1, valkey 4.1.3).
- **The k6 load profile (`tests/load`) has never been run.** It needs a deployed, seeded instance
  and a control-plane API key; the release workflow runs it only when a staging target is
  configured and otherwise marks the release as pre-release.
- **SharpFuzz is not wired up.** The fuzz targets exist and are driven by a property-based test;
  coverage-guided fuzzing needs a driver project and instrumentation that do not exist yet.
- **The eight-device manual matrix (§D.2)** has not been run.
- **No external penetration test (S-10) and no staging key-rotation exercise (S-12)** have taken
  place; both are release-blocking for 1.0.

### Security

- Dependencies are locked and restored in locked mode; GitHub Actions are pinned to commit SHAs;
  releases are signed keyless with Sigstore and ship SBOM, CBOM and SLSA provenance (T-14, S-06).

[Unreleased]: https://github.com/MAGORS-organisation/Deep-Links-Engine/compare/master...develop
