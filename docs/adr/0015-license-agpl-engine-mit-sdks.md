# ADR-0015 — Licence: AGPL-3.0 for the engine, MIT for the SDKs

**What this is:** the licence decision that replaces [ADR-0011](0011-license-mit.md) (MIT for the whole repository).
**Who it is for:** contributors, potential adopters with licence policies, and anyone who wants to build on this code.

## Status

**Accepted.** Supersedes [ADR-0011](0011-license-mit.md). Returns the engine to the specification's original recommendation, AGPL-3.0, while keeping the SDKs permissive.

## Date

Decided by the owner and recorded on 2026-11-16, before the 1.0 release and before any external contribution.

## Context

ADR-0011 chose MIT for adoption and zero legal friction, and stated the price plainly: anyone may run the engine as a closed, paid hosted service, modify it and publish nothing. Every line of the repository so far is the owner's, so the licence of future versions is still the owner's to choose.

The SDKs are a different kind of code. They are compiled into other people's applications, most of them closed-source. A copyleft licence on an SDK would make it unusable for exactly the integrators the engine exists for.

## Decision

- **Engine — everything outside `sdk/`** (edge, control plane, admin console, deployment assets, tests and tooling): **GNU AGPL-3.0-only**, in [`LICENSE`](../../LICENSE). Anyone who runs a modified engine as a network service must offer its users the modified source.
- **SDKs — everything in `sdk/`** (web, Android, iOS and their samples): **MIT**, in `sdk/web/LICENSE`, `sdk/android/LICENSE` and `sdk/ios/LICENSE`, so they can be embedded in any application.
- [`NOTICE`](../../NOTICE) states the copyright holder and the split.
- **"-only", not "-or-later":** whether a future GNU licence version applies stays the project's decision.
- **Metadata follows the split.** NuGet packages, the admin console's `package.json`, the Helm chart and the `dle-edge` / `dle-control` image labels say `AGPL-3.0-only`. The web SDK's `package.json` and the Android POM say `MIT`. The `dle-postgres` image contains no project code and is labelled `PostgreSQL`.
- **Contributions** are accepted under the licence of the part they change (inbound = outbound): AGPL-3.0-only outside `sdk/`, MIT inside it. Whether to require a contributor licence agreement — needed only if a commercial licence is ever to be offered for the engine — is decided before the first external contribution is accepted.
- **Dependencies** stay permissive. The CI licence gate keeps its allow-list: the SDKs must remain redistributable under MIT, and permissive dependencies are compatible with the AGPL engine.

## Consequences

| Gained | Given up |
|---|---|
| A hosted fork of a modified engine must publish its changes; improvements flow back | Some companies have a blanket "no AGPL" rule and will not run the engine — the SDKs are not affected |
| The SDKs stay embeddable in any application, closed-source included | Two licences to explain instead of one |
| A path to dual licensing stays open, if a contributor licence agreement is introduced in time | Versions published before this decision remain available under MIT to whoever received them; a licence change is not retroactive |

**Swift Package Manager.** The iOS SDK is resolved from this repository's root `Package.swift`, so tools that read a package's root `LICENSE` report AGPL-3.0 for it even though `sdk/ios` is MIT (`sdk/ios/LICENSE`). Publishing each SDK from its own repository removes the ambiguity.

## Alternatives considered

| Alternative | Why it lost |
|---|---|
| Keep MIT ([ADR-0011](0011-license-mit.md)) | No protection against a closed hosted fork |
| Apache-2.0 | The same permissiveness as MIT with a patent grant; it does not address the hosted-fork case either |
| AGPL-3.0 for the whole repository | Makes the SDKs unusable in closed-source applications |
| Business Source Licence / SSPL | Not open source by the OSI definition |

## References

- [`LICENSE`](../../LICENSE), [`NOTICE`](../../NOTICE), `sdk/web/LICENSE`, `sdk/android/LICENSE`, `sdk/ios/LICENSE`
- [ADR-0011](0011-license-mit.md), [`CONTRIBUTING.md`](../../CONTRIBUTING.md)
