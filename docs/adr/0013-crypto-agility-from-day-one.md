# ADR-0013 — Crypto agility from day one

**What this is:** the rule that every signed artefact names its algorithm and key, so that algorithms — including post-quantum ones — can change without a format change.
**Who it is for:** anyone touching `Dle.Crypto`, webhook consumers verifying signatures, and security reviewers.

## Status

Accepted

**Status note (2026-09-24).** The decision stands; the code does not meet it yet, in three places.

- **Signing-key rotation is not implemented.** Only the configuration-backed key store (`ConfigurationSigningKeyStore`) is registered; `Dle:Crypto:KeyRotationDays` is read only by `KeyRing.RotateAsync`, which nothing in the product calls; the `signing_keys` table is never used. The webhook Ed25519 key is derived from the master secret and never rotates. The rotation described under Consequences, and the bootstrap-key fix under Verification, exist in `KeyRing` and its unit tests only.
- **`click_id` carries no `alg` and no `kid`.** It is a Feistel-permuted timestamp and sequence followed by a truncated (32-bit) HMAC ([`ClickIdCodec`](../../src/Dle.Crypto/ClickIdCodec.cs)).
- **The edge receives the master secret** (Compose and Helm), and the control plane's webhook signing key and the key that wraps webhook secrets are derived from it too. The separation T-15 asks for — an edge that holds no signing keys — does not hold today.

See [Known gaps](../../README.md#known-gaps) in the root README.

## Date

Decided: in the specification ([§B.4 ADR-013](../zadanie.md#adr-013--krypto-agilita-od-začiatku), detail in [§E.5](../zadanie.md#e5-post-quantum-architektúra)) · Recorded: 2026-09-11

## Context

Post-quantum cryptography is not a redesign of this system, but it is a design decision that cannot be retrofitted cheaply: if a signature format has no room for an algorithm identifier, every consumer breaks when the algorithm changes. The engine signs three kinds of artefact — click tokens, webhook payloads and API tokens — and each has external consumers. [§E.5.1](../zadanie.md#e5-post-quantum-architektúra) sets out the regulatory timeline; [§E.5.2](../zadanie.md#e5-post-quantum-architektúra) what .NET 10 provides; [§E.5.4](../zadanie.md#e5-post-quantum-architektúra) the critical view — hybrid now, PQ signatures when the ecosystem is ready, no marketing.

## Decision

- **Every signed artefact carries `alg` and `kid` in its header.** No consumer may assume an algorithm from context.
- **The signing layer is an abstraction with multiple providers** (`ISigner` / `IVerifier` in `src/Dle.Crypto`), with key generation, rotation and distribution as a first-class component (C-12 Key Management) and a JWKS endpoint at **`/.well-known/jwks.json`** on the control host (`:8081`).
- **Webhook signature** ([§B.7.4](../zadanie.md#b74-webhook-postback)): header `DLE-Signature: t=<unix seconds>,v1=<HMAC-SHA256>,v2=<Ed25519>,kid=<key id>` plus `DLE-Alg` naming the algorithms. `v1` is the shared-secret path every consumer can verify today; `v2` is the asymmetric path verifiable against the JWKS; the format has a **reserved slot for a post-quantum signature** to be added later without a breaking change. Receivers reject a timestamp outside **±5 minutes** (replay window).
- **TLS on the edge is deployed with hybrid key exchange already in v1** — this is the TLS terminator's job (Caddy in Profile A, the ingress in Profile B), not the application's.
- A **CBOM** (cryptographic bill of materials, [§E.4.1](../zadanie.md#e41-inventár-kryptografických-prvkov-povinný-artefakt--cbom)) ships with every release alongside the SBOM ([NFR-13](../zadanie.md#a5-nefunkčné-požiadavky)).

## Consequences

- Positive: an algorithm swap is a new provider and a new `kid`; consumers that honour `alg` and `kid` need no change ([NFR-17](../zadanie.md#a5-nefunkčné-požiadavky)).
- Positive: key rotation is routine — publish the new key in the JWKS, sign with it, retire the old one after the overlap. (Not implemented as of 2026-09-24; see the status note.)
- Negative: two signatures per webhook delivery cost CPU and bytes; consumers must be told which to verify and how to fetch the JWKS.
- Negative: "reserved slot" is a promise about the format, not an implementation — no PQ signature provider exists in v1.
- Verification: `Dle.Crypto` is in the domain tier with ≥ 90 % coverage. One defect the suite found is exactly the kind this ADR is meant to prevent: **key rotation could not retire the bootstrap key**, so a leaked bootstrap key stayed valid forever while the operator saw a successful rotation. Fixed; in the commit history. Webhook signature and JWKS shapes are covered by the contract suite.

## Alternatives considered

| Alternative | Why it lost |
|---|---|
| A single HMAC signature with no header metadata (the common webhook pattern) | Cannot rotate algorithm or key without breaking every consumer at once |
| Ship a PQ signature (ML-DSA) in v1 | .NET 10 exposes the primitives, but consumer libraries and operational experience are not there; the specification prioritises hybrid TLS first and PQ signatures when the ecosystem allows ([§E.5.3](../zadanie.md#e5-post-quantum-architektúra)) |
| Asymmetric only (drop `v1`) | Every webhook consumer would need JWKS fetching on day one; the HMAC path keeps integration simple while `v2` is available for those who want it |

## References

- [docs/zadanie.md §B.4 ADR-013](../zadanie.md#adr-013--krypto-agilita-od-začiatku); [§E.4](../zadanie.md#e4-kryptografická-architektúra), [§E.5](../zadanie.md#e5-post-quantum-architektúra)
- [docs/zadanie.md §B.7.4](../zadanie.md#b74-webhook-postback) — webhook contract; [§C.3.4](../zadanie.md#c34-krypto-agilný-podpis) — the signing pattern
- `src/Dle.Crypto`; JWKS at `/.well-known/jwks.json` on `:8081`
