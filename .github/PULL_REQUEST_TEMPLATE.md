<!--
Target branch: `develop` (integration). `master` is reserved for releases.
Keep the description short; the checklist below is the Definition of Done from docs/zadanie.md §C.7
and is what reviewers check. Delete lines that genuinely do not apply and say why in one clause.
-->

## What and why

<!-- One paragraph. Link the issue (#123) or the spec requirement (FR-/NFR-/S-) this implements. -->

## How it was verified

<!-- Which suites ran locally (see CONTRIBUTING.md for the exact commands), what you tried by hand. -->

## Definition of Done (§C.7)

- [ ] **Tests** — unit tests for domain logic; an integration test (Testcontainers) for every endpoint touched.
- [ ] **Analyzers and format** — `dotnet build Dle.sln -warnaserror` and `dotnet format Dle.sln --verify-no-changes` are clean.
- [ ] **Resolve-path latency** — not regressed (benchmark gate, +5 % tolerance). If the change is on the resolve path, say what you measured.
- [ ] **API surface** — OpenAPI description and XML doc comments for anything public (`GenerateDocumentationFile`).
- [ ] **CHANGELOG** — entry under *Unreleased* in CHANGELOG.md.
- [ ] **Schema** — if the change touches the database: an EF Core migration **and a rollback note** in the PR description.
- [ ] **Security (§E.8)** — no new SAST findings (CodeQL), no new High/Critical CVE (Security workflow), threat model updated if the architecture changed (S-09).
- [ ] **Real devices** — if the change touches routing, the interstitial, or the `/.well-known` files: **tested by hand on a real iOS device and a real Android device** (state models and OS versions; simulators do not count).
- [ ] **Privacy** — nothing new is collected, stored or transmitted without a consent mode that permits it; no PII in logs at `Information` or below (S-08).

## Rollback note (schema changes only)

<!-- How to revert this migration on a live instance, and what data it would lose. -->

## Screenshots (admin console changes only)
