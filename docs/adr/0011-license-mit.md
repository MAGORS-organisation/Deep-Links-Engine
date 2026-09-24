# ADR-0011 — Licence: MIT

**What this is:** the one deviation from the specification that is recorded as a decision, and the trade-off that was accepted in doing so. It is not the only departure; the others are listed in [the ADR index](README.md#departures-not-recorded-as-decisions).
**Who it is for:** contributors, potential adopters with licence policies, and anyone who later wants to build a business on this code.

## Status

**Accepted, deviation.** The specification ([§B.4 ADR-011](../zadanie.md#adr-011--licencia-agpl-30--cla)) recommended **AGPL-3.0 with a contributor licence agreement**. The project owner chose **MIT**. See [`LICENSE`](../../LICENSE).

## Date

Recommended in the specification (as an open question, [§F.2](../zadanie.md#f2-otvorené-otázky-ktoré-musíš-rozhodnúť-ty)) · Decided by the owner before the first external contribution · Recorded: 2026-09-11

## Context

The specification put the licence among the decisions that must be taken **before the first line is written**, because changing a licence after accepting external contributions is practically impossible without a CLA. It laid out both sides:

- **For AGPL-3.0 + CLA:** AGPL prevents someone taking the code, building a hosted SaaS on it and giving nothing back — the model Dub chose. A CLA is required if a commercial licence is ever to be sold to a company that cannot use AGPL (dual licensing).
- **Against:** AGPL deters corporate adoption. Many companies have an internal rule of "no AGPL code", and will not discuss it. If the goal is maximum adoption and reputation, a permissive licence is better. If the goal is to build a business on it, AGPL + CLA.

The owner's goal is adoption and zero legal friction for the European company with an app, a compliance officer and a finance director that the root README describes.

## Decision

The whole repository — services, SDKs, console, deployment assets — is licensed under the **MIT License**. No CLA. Contributions are accepted under the same licence (inbound = outbound).

## Consequences

Stated plainly, as the specification asked ([§F.3](../zadanie.md#f3-gono-go-odporúčanie)):

| Gained | Given up |
|---|---|
| Adoption without a legal review: MIT passes every corporate allow-list | **No protection against a closed hosted fork.** Anyone may run this as a paid SaaS, modify it, and publish nothing |
| Zero friction for contributors: no CLA to sign, no copyright assignment | **No dual-licensing path.** Without a CLA the project cannot later sell a commercial licence, and cannot relicense once external contributions exist |
| The SDKs can be embedded in any app, including closed-source ones, with nothing more than attribution | The "give back" incentive is social, not legal |
| Comparison table in the root README reads MIT against Branch/AppsFlyer (commercial) and Dub (AGPL + commercial) — the adoption argument is clean | — |

This is a business decision, not a technical one, and it is reversible only in one direction: the project can never become more restrictive than MIT for the code already contributed.

**Effect on [ADR-0005](0005-hybridcache-and-valkey.md).** With MIT the question "would an AGPL dependency contaminate our licence?" disappears — and the specification was already careful that it never was a contamination question (a client that connects to an AGPL Redis over the network does not inherit its licence). The reason Valkey stays is the other half of the specification's argument: **we ship the L2 inside the `docker compose` and Helm bundle**, and shipping AGPL software in that bundle both blurs the boundary and triggers the "no AGPL in the stack" rule at exactly the companies MIT was chosen to reach. Valkey (BSD-3-Clause) removes that conversation. It is about what we distribute, not about contamination.

**Effect on [NFR-18](../zadanie.md#a5-nefunkčné-požiadavky) (licence cleanliness).** The CI licence gate now checks compatibility with MIT, which every dependency in the solution satisfies; the check remains so that a future copyleft dependency is caught rather than assumed.

## Alternatives considered

| Alternative | Why it lost |
|---|---|
| AGPL-3.0 + CLA (the specification's recommendation) | Protects against a SaaS fork and keeps a dual-licensing option — at the cost of the corporate adoption the owner prioritises, and of a CLA process for every contributor |
| Apache-2.0 | The specification's own suggestion if adoption is the goal; it adds an explicit patent grant and a NOTICE requirement. The owner preferred the shorter, better-known MIT; the practical difference for adopters is small |
| Business Source Licence / SSPL | Not open source by the OSI definition; contradicts the project's positioning |

## References

- [docs/zadanie.md §B.4 ADR-011](../zadanie.md#adr-011--licencia-agpl-30--cla), [§F.2](../zadanie.md#f2-otvorené-otázky-ktoré-musíš-rozhodnúť-ty), [§F.3](../zadanie.md#f3-gono-go-odporúčanie)
- [`LICENSE`](../../LICENSE), [`CONTRIBUTING.md`](../../CONTRIBUTING.md)
- Root README, [Licence](../../README.md#licence) and [Compared with the alternatives](../../README.md#compared-with-the-alternatives)
