# Documentation

**What this is:** the map of everything under `docs/` — one line per directory, in the order a new reader needs it.
**Who it is for:** anyone who arrived from the [root README](../README.md) and wants to know where a specific answer lives.

## Map

| Directory | What is in it | Start with |
|---|---|---|
| [self-hosting](self-hosting) | Quick start, configuration, domains, upgrading, backup, troubleshooting | [quickstart.md](self-hosting/quickstart.md) — the first 30 minutes |
| [integration](integration) | HTTP API, the three SDKs, webhooks, migrating from Firebase Dynamic Links | the API reference; the live one is served at `/scalar/v1` on the control host |
| [compliance](compliance) | Privacy model, DPIA and RoPA templates, regulatory map, security overview | the privacy model — it explains the three consent modes |
| [operations](operations) | Runbook, release checklist, device test matrix | the runbook |
| [adr](adr) | The thirteen architecture decisions, the one deviation from the specification recorded as a decision (the licence), and the departures that are not recorded as decisions | [adr/README.md](adr/README.md) — process, status lifecycle, index, departures |
| [architecture](architecture) | The three product layers, two deployment units, request flows, data model, routing-rule language, trust boundaries, observability, performance targets | [architecture/README.md](architecture/README.md) |
| [dev](dev) | [SHARED-KERNEL.md](dev/SHARED-KERNEL.md), the binding contract every project codes against; [API-INVENTORY.md](dev/API-INVENTORY.md); per-project working notes | SHARED-KERNEL.md |
| [zadanie.md](zadanie.md) | The Slovak specification everything traces back to. Every document here links the exact section it derives from | §0 (management summary) and [§F.3](zadanie.md#f3-gono-go-odporúčanie) (go/no-go) |

Outside `docs/` but part of the same set:

| Path | What it is |
|---|---|
| [../deploy/README.md](../deploy/README.md) | Deployment: Compose (Profile A) and the Helm chart (Profile B). Not duplicated here |
| [../.github/workflows](../.github/workflows) | CI: build and tests, CodeQL, SBOM, security, nightly, release, and one workflow per SDK |
| [../CONTRIBUTING.md](../CONTRIBUTING.md) · [../SECURITY.md](../SECURITY.md) | Contribution and vulnerability-disclosure policies |

## Reading paths

| I want to… | Read, in this order |
|---|---|
| run it | [self-hosting/quickstart.md](self-hosting/quickstart.md) → [../deploy/README.md](../deploy/README.md) → self-hosting configuration and domains |
| integrate an app | integration (API, then the SDK for your platform, then webhooks) → [architecture/request-flows.md](architecture/request-flows.md) so you know what happens on a click |
| write routing rules | [architecture/routing-rules.md](architecture/routing-rules.md) |
| audit it | compliance → [architecture/context.md](architecture/context.md) (trust boundaries) → [adr](adr) |
| change it | [dev/SHARED-KERNEL.md](dev/SHARED-KERNEL.md) → [architecture](architecture) → [adr](adr) → [../CONTRIBUTING.md](../CONTRIBUTING.md) |

## Conventions every document follows

- Opens with two lines: what it is, who it is for.
- Links the exact `zadanie.md` section for anything you may want to challenge, e.g. [§E.6.2](zadanie.md#e62-tri-režimy-prevádzky-produktová-funkcia-nie-prepínač-v-kóde).
- Wire JSON is `snake_case`. Errors are RFC 9457 problem documents; the problem types live in [`src/Dle.Domain/Contracts/ProblemCodes.cs`](../src/Dle.Domain/Contracts/ProblemCodes.cs).
- Ports: **edge `:8080`** (`/{slug}`, `/{slug}/qr`, `/.well-known/apple-app-site-association`, `/.well-known/assetlinks.json`); **control `:8081`** (`/api/v1/*`, `/v1/resolve`, `/v1/events`, `/scalar/v1`, `/.well-known/jwks.json`).
- A link is never answered with `301` ([ADR-0009](adr/0009-http-response-shape-never-301.md)); `/.well-known/*` is never redirected ([§A.2.1](zadanie.md#a21-apple-universal-links)).

## What is verified and what is not

The documents describe the design as built. Where the code falls short of what a document or an ADR says, the document says so (in the ADRs, as a dated status note); the gaps that matter to an operator or integrator are listed in the root README under [Known gaps](../README.md#known-gaps). As of 2026-09-24, in CI: the .NET solution (14 projects) builds with `-warnaserror` at 0/0; the unit, contract, security and integration suites pass (the last against PostgreSQL 18 and Valkey 8 in Testcontainers, including the migration, its rollback and the PostgreSQL and Valkey chaos scenarios); both hosts start; the web SDK passes its tests and its bundle-size budget; the admin console builds; the Android and iOS SDKs compile and pass their unit tests; both container images build and pass Trivy. The OWASP ZAP baseline runs against the development Compose override with an empty database and scans the edge directly, so it only ever sees the 404 page and `robots.txt`; Caddy, TLS, redirects, interstitials and `/.well-known` are not exercised there. Test counts and coverage figures are in the CI summary; the coverage gate holds the domain tier at ≥ 90 % and reports overall coverage against a 70 % target. Written but **not** verified: the k6 load profile, which cannot pass as written ([tests/load/README.md](../tests/load/README.md)), and the 8-device manual matrix (pending). The full table is in the root README under [Where this stands](../README.md#where-this-stands).
