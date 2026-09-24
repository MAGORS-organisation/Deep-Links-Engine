# Data protection impact assessment — template

**What this is:** a DPIA pre-filled with this system's processing, ready for the operator to complete.
**Who it is for:** the operator's DPO or privacy lead. Placeholders are marked `[…]`. Systematic
monitoring of behaviour makes a DPIA practically mandatory under GDPR art. 35; the specification ships
this template for that reason ([§E.6.3](../zadanie.md)).

## 1. Controller and scope

| | |
|---|---|
| Controller | `[legal entity]` |
| DPO / contact | `[name, e-mail]` |
| System | Deep Link Engine, self-hosted, version `[x.y.z]` |
| Assessment date / next review | `[date]` / `[date + 12 months, or on any consent-mode change]` |
| Consent mode in force | `[off | aggregate_only | full]` — see [privacy.md](privacy.md) |

## 2. Description of the processing

**Purpose.** Resolve branded links to the correct app screen or web page; on the operator's request,
attribute app installs to the click that led to them; report aggregate campaign performance.

**Data subjects.** People who click the operator's links; people who install and open the operator's app.

**Data, by consent mode.**

| Data item | `off` | `aggregate_only` | `full` |
|---|---|---|---|
| Click counter per link | ✅ | ✅ | ✅ |
| Country, device class, OS family, browser family | ❌ | ✅ | ✅ |
| IP address | ❌ | HMAC with a salt rotated daily; never stored raw | hash as before, plus /24 (IPv4) or /48 (IPv6) prefix |
| Click id linking a click to an install | ❌ | ❌ | ✅ with recorded consent |
| Device signals for probabilistic matching (language, screen, timezone) | ❌ | ❌ | ✅ only if the module is enabled **and** consent recorded |
| Installation id (SDK-generated UUID, not a device id) | ✅ | ✅ | ✅ — the Android and iOS SDKs send it regardless of consent and the server stores it in every mode ([README — Known gaps](../../README.md#known-gaps)) |
| Full user agent, full referrer, full query string | never | never | never |

**Retention.** Raw click events `[30]` days, aggregates `[730]` days, attributions `[…]`. The retention
worker logs its own runs, so the schedule is auditable.

**Recipients.** Webhook endpoints configured by the operator: `[list]`. No transfer to the software
vendor. No transfer outside the EU unless the operator configures one: `[none | describe]`.

## 3. Necessity and proportionality

| Question | Answer to complete |
|---|---|
| Is attribution necessary for the stated purpose, or would aggregate counts suffice? | `[…]` — if aggregates suffice, `aggregate_only` is the proportionate choice |
| Legal basis | `off`, `aggregate_only`: legitimate interest — attach the LIA. `full`: consent under ePrivacy art. 5(3) as read by EDPB Guidelines 2/2023, recorded with timestamp |
| Data minimisation measures already in the system | User agent reduced to families; referrer reduced to host; query parameters allow-listed; IP hashed or dropped; audit log structurally unable to hold an end-user identifier |
| Transparency | `[link to privacy notice]` naming recipients and the consent mode |
| Rights | No erasure endpoint yet (planned in §E.6.3) and no per-subject access endpoint: erasure and per-subject access by `install_id` are SQL work — see [privacy.md — Data-subject rights](privacy.md#data-subject-rights); export of a tenant's data (`GET /api/v1/exports/tenant`); objection honoured by switching the subject's consent to `attribution = false`, after which no attribution signals are stored (`install_id` still is) |

## 4. Risks and mitigations

| # | Risk | Likelihood | Severity | Mitigation in the system | Residual |
|---|---|---|---|---|---|
| R1 | Re-identification from hashed IP | Low for anyone without the master secret — hash is keyed. Not low for the operator: every daily salt is derived from the master secret and can be recomputed | Medium | Daily salt change; no raw IP; prefix only in `full`; raw events dropped after `RawDays` (30 by default) | `[…]` |
| R2 | Cross-tenant data leak | Low with one tenant or mutually trusted tenants; see the known defect | High | Tenant filter enforced in the persistence layer (no PostgreSQL row-level security); cross-tenant ids answer 404; tested. Known defect: a request carrying an SDK key of one tenant and an API key of another is scoped to the first and authorised as the second — do not host mutually untrusted tenants on one instance ([README — Known gaps](../../README.md#known-gaps)) | `[…]` |
| R3 | Probabilistic match attributes the wrong person | Medium if enabled | Medium | Off by default; consent-gated; 60-minute window; confidence recorded and shown; evidence stored per attribution | `[…]` |
| R4 | Retention exceeded | Low | Medium | Automated partition drop; job logs its execution | `[…]` |
| R5 | Link used for phishing exposes subjects to harm | Medium | High | Target URL policy at creation and nightly; reputation checks only once a source is configured (off by default); rate limits; quarantine (effective at the edge after its cache expires, up to 10 min 30 s by default). The public abuse form is not reachable with the shipped routing ([README — Known gaps](../../README.md#known-gaps)) | `[…]` |
| R6 | Loss of confidentiality of the database | Low | High | `[operator's encryption at rest, access control, backup encryption]` | `[…]` |
| R7 | Consent recorded incorrectly by the app | Medium | High | Consent object carries a timestamp and source; server refuses signals without it — but the app's consent UI is the operator's responsibility | `[…]` |

## 5. Consultation and sign-off

| | |
|---|---|
| DPO opinion | `[…]` |
| Data subjects consulted / representative views | `[…]` |
| Prior consultation with the supervisory authority required (art. 36)? | `[yes/no, reasoning]` |
| Approved by | `[name, role, date]` |
