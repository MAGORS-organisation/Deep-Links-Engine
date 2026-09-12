# ADR-0008 — Deferred deep linking strategies

**What this is:** the four attribution strategies, their order, and why the probabilistic one is off by default.
**Who it is for:** product people comparing this with an MMP, compliance officers, and SDK integrators.

## Status

Accepted

## Date

Decided: in the specification ([§B.4 ADR-008](../zadanie.md#adr-008--stratégia-deferred-deep-linkingu)) · Recorded: 2026-09-11

## Context

"Deferred deep linking" means carrying the link context through an app-store install. Android has a deterministic channel for it (the Play Install Referrer, [§A.2.4](../zadanie.md#a24-odovzdávanie-kontextu-cez-inštaláciu)); iOS has none. The industry fills the gap with probabilistic device fingerprinting, which is legally exposed — ePrivacy Article 5(3) and the final EDPB Guidelines 2/2023 put it under consent — and technically unreliable: Safari's Advanced Fingerprinting Protection in iOS 26, and accuracy that degrades below usefulness after 24 hours ([§A.2.5](../zadanie.md#a25-presnosť-probabilistického-párovania--čísla)). Building the product on that would be building on sand.

## Decision

The engine supports **four strategies**; the order is configurable per tenant and the default is from strongest to weakest:

| # | Strategy | Platform | Deterministic | Friction |
|---|---|---|---|---|
| S1 | **Install Referrer** | Android | ✅ 1.00 | none |
| S2 | **Login reconciliation** | both | ✅ 1.00 | requires sign-in |
| S3 | **Claim code** | both (mainly iOS) | ✅ 1.00 | the user types a 6-character code |
| S4 | **Probabilistic match** | both | ⚠️ 0.3–0.9 | none, but **requires consent and is off by default** |

Plus **S0 — direct open**: if the app is installed, a Universal Link / App Link opens it directly and the SDK reports the URL back (`POST /v1/events { type: "link_open" }`). That is the most common and fully deterministic case, and the one "deferred" discussions forget ([§B.6.4](../zadanie.md#b64-priame-otvorenie-najčastejší-prípad-ktorý-sa-zabúda)).

Every attribution record carries `match_type` ∈ `install_referrer` · `login` · `claim_code` · `direct_open` · `probabilistic` · `none` and a `confidence` (1.00 for deterministic), plus an `evidence` document saying what decided it. The probabilistic module, when enabled, matches only within a **≤ 60-minute** window on IP prefix + OS + language + time — not 7 days.

## Consequences

- Positive: the dashboard can be honest — *"78 % of attributions deterministic, 14 % probabilistic (mean confidence 0.71), 8 % unmatched"* — which is the selling point against MMPs that blur the distinction systematically.
- Positive: with the module off, the resolve path stores no fingerprint-grade signal at all; consent mode `aggregate_only` (the default) needs no consent dialogue ([§E.6.2](../zadanie.md#e62-tri-režimy-prevádzky-produktová-funkcia-nie-prepínač-v-kóde)).
- Negative: on iOS, without login or a claim code, the honest answer is often `match_type: "none"`. The product says so rather than inventing a match.
- Negative: an operator turning S4 on takes on the consent obligation; the engine records the consent but cannot obtain it.
- Verification: the attribution matcher is in the domain tier (≥ 90 % coverage); the `/v1/resolve` and `/v1/events` endpoints are covered by contract and security tests. The mobile SDKs that report S0/S1/S3 are **written but not compiled** — CI is their first build.

## Alternatives considered

| Alternative | Why it lost |
|---|---|
| Fingerprinting as the default (the MMP model) | Consent-gated under ePrivacy 5(3) / EDPB 2/2023; accuracy below a coin toss past 24 h; blocked increasingly by the platforms |
| Clipboard-based pasteboard matching on iOS | Triggers the iOS pasteboard warning; the SDKs deliberately never read the clipboard |
| Deterministic-only, no probabilistic module at all | Some operators have consent and a legitimate short-window use; leaving it out pushes them to a fingerprinting vendor. Off by default is the compromise |

## References

- [docs/zadanie.md §B.4 ADR-008](../zadanie.md#adr-008--stratégia-deferred-deep-linkingu)
- [docs/zadanie.md §B.6.2](../zadanie.md#b62-deferred-deep-link--android-deterministický), [§B.6.3](../zadanie.md#b63-deferred-deep-link--ios-bez-determinizmu-z-platformy), [§B.6.4](../zadanie.md#b64-priame-otvorenie-najčastejší-prípad-ktorý-sa-zabúda) — the flows, drawn in [architecture/request-flows.md](../architecture/request-flows.md)
- [docs/zadanie.md §E.6](../zadanie.md#e6-súkromie-a-ochrana-údajov-privacy-by-design) — the privacy model
- `attributions` table: [architecture/data-model.md](../architecture/data-model.md)
