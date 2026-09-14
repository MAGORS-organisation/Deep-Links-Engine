# Contributing to Deep Link Engine

DLE is a self-hosted, EU-first deep-linking and attribution engine. It is MIT licensed, there is
no CLA, and the specification the code is written against is `docs/zadanie.md` — when a pull
request and the spec disagree, the PR description has to say which one is wrong and why.

## Branches

- `develop` — integration branch. **All pull requests target `develop`.**
- `master` — reserved for releases; it moves only when a release is cut.
- Feature branches: `feat/<topic>`, `fix/<topic>`, `build/<topic>`, `docs/<topic>`.

Commits follow Conventional Commits (`feat(control): …`, `fix(edge): …`, `test: …`, `build: …`,
`ci: …`, `docs: …`), which is what the existing history uses and what the changelog is assembled
from.

## Prerequisites

| Area | Needs |
|---|---|
| .NET solution | .NET SDK **10.0.400 or later 10.0.x** (`global.json`, roll-forward `latestFeature`) |
| Integration tests | Docker (Testcontainers pulls `postgres:18` and `valkey/valkey:8`) |
| Admin console, Web SDK | Node **22** (Node 20 is the minimum for the console, 18 for the SDK) |
| Android SDK | JDK 17, Android SDK (compileSdk 35), Gradle **8.11.1** — there is no `gradlew` in the repo; install Gradle or use `gradle/actions/setup-gradle` as CI does |
| iOS SDK | Xcode **16.4** (Swift 6, swift-tools 6.0) on macOS |
| Compose stack | Docker Compose v2 |
| Helm chart | Helm 3 |

Everything in the .NET solution is also driven from `deploy/aspire` for local development; see
that directory. The compose stack is `deploy/docker-compose.yml` (+ `docker-compose.override.yml`
for local ports and plain HTTP).

## Building

```sh
dotnet restore Dle.sln --locked-mode          # locked restore is verified at SOLUTION level only
dotnet build   Dle.sln -warnaserror --no-restore
dotnet format  Dle.sln --verify-no-changes --no-restore
```

`TreatWarningsAsErrors=true` and `AnalysisLevel=latest-Recommended` come from
`Directory.Build.props`; the build must be 0 warnings / 0 errors. Do not add `NoWarn` entries to
make it so — fix the code, or explain in the PR why the analyzer is wrong for this case.

Dependencies are centrally managed (`Directory.Packages.props`) with a `packages.lock.json` per
project. Adding or bumping a package means running `dotnet restore Dle.sln` **without**
`--locked-mode` once, then committing the updated lock files. CI restores in locked mode and fails
if they are stale.

EF Core: `dotnet tool restore` then `dotnet ef … --project src/Dle.Persistence`. Every migration
comes with a rollback note in the PR.

## Running the test suites

The test projects are **xunit.v3 in-process runners** (`OutputType=Exe`). They are run with
`dotnet run`, not `dotnet test`. The TRX switch is `-result-trx <file>`.

```sh
dotnet run --project tests/Dle.UnitTests        -c Release -- -result-trx TestResults/Dle.UnitTests.trx
dotnet run --project tests/Dle.ContractTests    -c Release -- -result-trx TestResults/Dle.ContractTests.trx
dotnet run --project tests/Dle.SecurityTests    -c Release -- -result-trx TestResults/Dle.SecurityTests.trx
dotnet run --project tests/Dle.IntegrationTests -c Release -- -result-trx TestResults/Dle.IntegrationTests.trx
```

Filters use the runner's own syntax, e.g. `-class "Dle.SecurityTests.Fuzzing.FuzzHarnessTests"`,
`-namespace "Dle.UnitTests.Abuse"`, `-method "…"`, or `-filter "/*/Dle.UnitTests.Abuse/*"`.
`-- --help` prints the full list.

`Dle.IntegrationTests` needs Docker. Without it the suite **skips** every test and exits green,
which is the wrong answer in CI, so CI sets `DLE_TESTS_REQUIRE_DOCKER=1` and the suite fails
loudly instead. Set the same variable locally if you want the same guarantee.

Coverage is collected in CI with `dotnet-coverage` and gated by `.github/scripts/coverage-gate.py`:
`Dle.Domain` and `Dle.Crypto` must stay at or above 90 % line coverage (hard); the product as a
whole is reported against 70 % (warning until it gets there).

Front-end and SDKs:

```sh
cd src/Dle.Admin.Web && npm ci && npm run lint && npm run build
cd sdk/web           && npm ci && npm run lint && npm run typecheck && npm run build && npm test && npm run size
cd sdk/android       && gradle :dle-sdk:assembleRelease :dle-sdk:testReleaseUnitTest :sample:assembleDebug
cd sdk/ios           && xcodebuild build-for-testing -scheme DleSDK-Package -destination 'platform=iOS Simulator,name=iPhone 16' \
                     && xcodebuild test-without-building -scheme DleSDK-Package -destination 'platform=iOS Simulator,name=iPhone 16'
```

Load profile (`tests/load`) and the k6 gate are documented in `tests/load/README.md`; they need a
deployed, seeded instance and have not been run in this repository's development environment.

## Definition of Done

From `docs/zadanie.md` §C.7 — the PR template repeats it as a checklist:

1. Unit tests for domain logic and an integration test (Testcontainers) for the endpoint.
2. `dotnet format` and analyzers clean (`TreatWarningsAsErrors`, `latest-Recommended`).
3. Resolve-path latency not regressed (benchmark gate, +5 % tolerance).
4. OpenAPI description and XML doc comments for public surface.
5. A CHANGELOG entry; for schema changes, a migration plus a rollback note.
6. §E.8 security criteria met: no new SAST findings, no new High/Critical CVE.
7. Routing or `/.well-known` changes: **tested by hand on a real iOS and a real Android device.**
   Simulators do not exercise Universal Links / App Links verification.

## Architecture decisions

Decisions that constrain future work are recorded as ADRs. The code and tests already cite them
by number (ADR-009: 302-not-301 and Open Graph for crawlers, for instance), but the ADR directory
has not been created yet — the first PR that adds one creates `docs/adr/` and starts with
`0001-record-architecture-decisions.md`.

Process:

- One file per decision: `docs/adr/NNNN-short-title.md`, sequential number, never reused.
- Sections: **Context**, **Decision**, **Consequences**, **Status** (proposed → accepted →
  superseded by NNNN). Keep it to a page.
- An ADR is proposed in the PR that needs it and accepted by merging that PR. Superseding an ADR
  means a new ADR, not editing the old one.
- Security-relevant decisions also update the threat model (§E.8 S-09 is a review gate).

`docs/` itself is owned by the specification; ADRs are the one thing contributors add there.

## Security

Vulnerabilities go through private vulnerability reporting, never through issues or PRs — see
[SECURITY.md](SECURITY.md). If a PR fixes a vulnerability, coordinate with the maintainers before
opening it publicly.

Things reviewers will refuse on sight: fingerprinting of end-user devices, calls to third-party
services from SDKs or the interstitial, new identifiers that do not depend on a consent mode,
logging of PII at `Information` or below, and a dependency without a lock-file update.

## Licence

MIT, see [LICENSE](LICENSE). There is no contributor licence agreement: by opening a pull request
you agree that your contribution is licensed under the same MIT licence as the project
(inbound = outbound). Do not contribute code you do not have the right to license that way, and
do not add dependencies under licences incompatible with MIT — the Security workflow's licence
policy will reject them.

## Code of conduct

This project follows the [Contributor Covenant 2.1](CODE_OF_CONDUCT.md).
