# Regulatory map

**What this is:** which EU rules touch a deployment of this engine, how, and what the operator has to do
about each. **Who it is for:** the operator's data-protection and compliance functions. Source:
[docs/zadanie.md §E.6.4](../zadanie.md). This is orientation, not legal advice; two items below are
flagged as needing counsel.

| Regulation | Applies | How | What it means for an operator |
|---|---|---|---|
| **GDPR** | Directly | An IP address is personal data (*Breyer*, C-582/14). Click events, installs and attributions are processing of personal data. *EDPS v SRB* (C-413/23) means pseudonymised data may not be personal for a recipient who cannot re-identify — but the operator, who holds the key, always can | Choose a consent mode and its legal basis ([privacy.md](privacy.md)); keep the RoPA ([ropa-template.md](ropa-template.md)); run a DPIA ([dpia-template.md](dpia-template.md)); honour erasure via the `install_id` and IP-hash endpoints; default retention 30 days raw / 730 aggregated |
| **ePrivacy Directive art. 5(3)** | Directly, and more strictly than GDPR | The final EDPB Guidelines 2/2023 (October 2024) extend the consent requirement beyond cookies to **URL-parameter tracking and link decoration** — which is exactly what a click id in a link is — as well as IP-based tracking and fingerprinting. The "strictly necessary" exemption does not cover marketing attribution | `aggregate_only` (the default) avoids cross-session linking and stands on legitimate interest, which you must document in an LIA. `full` requires recorded consent before any click-id linking or probabilistic matching; the engine refuses to store signals without it |
| **Digital Services Act art. 16** | Probably, as a hosting service — **confirm with counsel** | A link shortener carrying user-created links is very likely an intermediary hosting service, which must offer a notice-and-action mechanism | The public abuse-report endpoint, the triage queue with recorded decisions and the quarantine record are that mechanism. Publish a contact point. Online-marketplace obligations do not apply |
| **NIS2** | Not directly | The engine is not DNS, a marketplace or a search engine | If a regulated customer runs it, they inherit their obligations and pass them contractually to whoever operates it. Expect security-policy and incident-reporting clauses |
| **DORA art. 30** | Indirectly, for financial-sector operators | ICT third-party contracts must provide audit rights, exit plans and data portability | The tenant export ([api.md](../integration/api.md)) is the exit plan's technical half; backup drills ([self-hosting/backup-restore.md](../self-hosting/backup-restore.md)) evidence RPO/RTO |
| **EU AI Act** | Not applicable | There is no AI system in the sense of the regulation; matching is rule-based scoring | Revisit if you add machine-learned fraud scoring |

## Roles

| Deployment | Operator's role | Vendor's role | Paperwork |
|---|---|---|---|
| Self-hosted (this repository) | **Controller** | None — the software vendor has no access to the instance | No data-processing agreement with the vendor. Your webhook recipients may be processors or independent controllers; name them in the RoPA |
| Hosted by a third party | Controller | **Processor** | A DPA under art. 28 is mandatory, and the transparency notice must name the processor and any sub-processors |

## Two questions that need a lawyer, not a README

1. Whether your deployment is a DSA hosting service (affects the notice-and-action and transparency
   obligations).
2. Whether the legitimate-interest assessment for `aggregate_only` holds for your specific use — the
   specification budgets roughly 2 000–4 000 EUR for both questions ([§F.2 Q4](../zadanie.md)).
