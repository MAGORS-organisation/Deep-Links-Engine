# Record of processing activities — template

**What this is:** the GDPR art. 30 record for a deployment of this engine, pre-filled. **Who it is
for:** the operator's DPO. Complete the `[…]` fields; keep one record per tenant if tenants are
separate legal entities.

| Field (art. 30(1)) | Entry |
|---|---|
| Controller | `[legal entity, address, contact]` |
| Joint controller / representative | `[if any]` |
| DPO | `[name, contact]` |
| **Processing activity** | Link resolution, deferred deep linking and install attribution for the controller's mobile application(s) |
| **Purposes** | (a) Deliver the user to the requested content in the app or on the web; (b) measure which link or campaign led to an install or conversion; (c) prevent abuse of the link domain; (d) operational statistics |
| **Categories of data subjects** | Persons clicking the controller's links; persons installing and using the controller's app |
| **Categories of personal data** | Depends on the consent mode in force: `[off / aggregate_only / full]`. See the table in [dpia-template.md](dpia-template.md) §2. Never: raw IP address, full user agent, full referrer, clipboard contents, advertising identifiers |
| **Categories of recipients** | Webhook endpoints: `[CRM / CDP / other, per recipient]`. The software vendor is **not** a recipient in a self-hosted deployment. Hosting provider: `[if the infrastructure is rented]` |
| **Transfers to third countries** | `[None]` by default — the resolve path makes no third-party call and GeoIP is an offline database. If a webhook endpoint or hosting is outside the EU: `[country, transfer mechanism]` |
| **Retention** | Raw click events `[30]` days; aggregates `[730]` days; attributions `[…]`; audit log (operator actions only, no end-user data) `[…]`; abuse reports `[…]` |
| **Technical and organisational measures (art. 32)** | IP hashing with daily salt rotation or no IP storage; consent-gated linking; tenant isolation enforced in the data layer; TLS with hybrid post-quantum key exchange where the proxy supports it; API keys stored as Argon2id hashes; signed webhooks; immutable audit log of administrative actions; automated retention; rate limiting and abuse quarantine; `[operator's own: access control, encryption at rest, backup encryption, incident process]` |
| **Legal basis** (recommended to record) | `off`, `aggregate_only`: legitimate interest, LIA dated `[…]`. `full`: consent (ePrivacy art. 5(3); GDPR art. 6(1)(a)), recorded per installation with timestamp |
| **Source of the data** | The data subject's device, via the browser request and the app SDK |
| **Automated decision-making** | None with legal or similarly significant effect. Routing decisions select content, not treatment of the person |
| **Last reviewed** | `[date]` |
