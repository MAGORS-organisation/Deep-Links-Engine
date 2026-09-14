# HTTP API reference

**What this is:** the endpoint map of both hosts, one `curl` per endpoint, the error contract (RFC 9457 problem types), `Idempotency-Key` semantics and the rate-limit table.
**Who it is for:** integrators writing against the API directly, and reviewers checking the implementation against [§B.7](../zadanie.md#b7-api-kontrakt). The live, always-current reference is the OpenAPI 3.1 document at `/scalar/v1` on the control plane.

## Conventions

| Topic | Rule |
|---|---|
| Wire JSON | `snake_case` everywhere — request, response, problem documents, webhook payloads |
| Versioning | In the path (`/api/v1`, `/v1`). A field may be added; an existing field never changes meaning within a major version |
| Timestamps | RFC 3339, UTC (`2026-09-11T10:00:00Z`) |
| Identifiers | Tenants, domains, apps, keys, webhooks: UUID. Links: an opaque string id; the slug is separate and is what appears in the URL |
| Paging | Cursor-based; `page_size` default 50, maximum 200 (`Dle:Control:DefaultPageSize` / `MaxPageSize`) |
| Redirect shape | A resolved link answers `302` or a `200` interstitial — **never `301`** (ADR-009). Everything cacheable about a link is cached server-side, never in the browser's permanent redirect cache |

## Two hosts

```mermaid
flowchart LR
  U[Users, bots, crawlers] -->|"GET /{slug}<br/>GET /.well-known/*<br/>GET /{slug}/qr"| E["dle-edge :8080"]
  S[Mobile & web SDKs] -->|"POST /v1/resolve<br/>POST /v1/events<br/>POST /v1/claim-codes"| C["dle-control :8081"]
  O[Operators, CI, console] -->|"/api/v1/*"| C
  W[Webhook receivers] -->|"GET /.well-known/jwks.json"| C
```

Behind Caddy (Profile A) or the Ingress (Profile B) both are one origin; the table below shows the routes as the reverse proxy exposes them.

## Edge — public data plane (`:8080`)

Spec: [§B.7.1](../zadanie.md#b71-verejné-data-plane). No authentication; rate-limited by client address.

| Method | Path | Answers |
|---|---|---|
| GET | `/{slug}` | `302` to the routed target, `200` interstitial for in-app webviews and app-not-installed cases, `200` OG page for crawlers, `404` unknown / foreign slug (same body and timing for both, T-07), `410` quarantined or expired |
| GET | `/{slug}/qr?format=svg&size=512` | `200 image/svg+xml` (or `png`) |
| GET | `/.well-known/apple-app-site-association` | `200 application/json`, generated per host, no redirect |
| GET | `/.well-known/assetlinks.json` | `200 application/json`, generated per host, no redirect |
| GET | `/healthz`, `/readyz` | Liveness / readiness (internal) |

```bash
curl -sI https://go.example.com/aB3xK9pQ                         # 302 Location: https://…
curl -s  https://go.example.com/aB3xK9pQ -A "Slackbot-LinkExpanding 1.0"   # 200, <meta property="og:title" …>
curl -sI "https://go.example.com/aB3xK9pQ?_dl=preview"           # FR-166 — full pipeline, no click recorded
curl -so qr.svg "https://go.example.com/aB3xK9pQ/qr?format=svg&size=512"
curl -s  https://go.example.com/.well-known/apple-app-site-association | jq .
curl -s  https://go.example.com/.well-known/assetlinks.json | jq .
```

The edge never calls a third party while answering — no GeoIP service, no reputation API, nothing (NFR-14). With PostgreSQL down it serves what is in cache and answers `503` for what is not.

## Control — SDK endpoints (`:8081`, `/v1`)

Spec: [§B.7.2](../zadanie.md#b72-sdk-api). Authentication: `Authorization: Bearer <sdk_key>` — the SDK key is a public identifier bound to a bundle id / package name / web origin (K6), issued with `POST /api/v1/apps/{id}/sdk-keys`.

### `POST /v1/resolve`

Called once per installation, on first launch. Returns the deferred context, or an honest `none`.

```bash
curl -s -X POST https://go.example.com/v1/resolve \
  -H "Authorization: Bearer dlk_…" -H "Content-Type: application/json" \
  -d '{
    "install_id": "9f2c0e1a-…",
    "platform": "android",
    "app_version": "3.4.1",
    "os_version": "15",
    "referrer": "dl_cid%3DaB3xK9pQ%26utm_source%3Dfb",
    "claim_code": null,
    "login_key": null,
    "signals": null,
    "consent": { "analytics": true, "attribution": true, "ts": "2026-09-11T10:00:00Z" }
  }'
```

```json
{
  "matched": true,
  "match_type": "install_referrer",
  "confidence": 1.0,
  "click_id": "aB3xK9pQ",
  "link": { "id": "…", "deeplink_path": "/promo/autumn", "campaign": "autumn26", "title": "Autumn promo" },
  "params": { "utm_source": "fb", "utm_campaign": "autumn26" },
  "expires_in": 0
}
```

`match_type` ∈ `install_referrer` · `login` · `claim_code` · `direct_open` · `probabilistic` · `none`. `signals` is only read when the tenant is in consent mode `full`, the probabilistic module is enabled, and `consent.attribution` is `true` with a timestamp — otherwise it is dropped before it reaches storage, not stored-then-deleted ([§E.6.2](../zadanie.md#e62-tri-režimy-prevádzky-produktová-funkcia-nie-prepínač-v-kóde)). A tampered `click_id` in the referrer (the token is HMAC-signed, T-05) yields `match_type: none`.

### `POST /v1/events`

Batched, at most **100 events** per call, answered `202 Accepted`. `link_open` is the event that makes direct opens countable — the OS hands an installed app the link without any HTTP request to the engine ([§B.6.4](../zadanie.md#b64-priame-otvorenie-najčastejší-prípad-ktorý-sa-zabúda)).

```bash
curl -s -X POST https://go.example.com/v1/events \
  -H "Authorization: Bearer dlk_…" -H "Content-Type: application/json" \
  -d '{
    "install_id": "9f2c0e1a-…",
    "platform": "ios",
    "app_version": "3.4.1",
    "events": [
      { "type": "link_open",  "url": "https://go.example.com/aB3xK9pQ", "ts": "2026-09-11T10:00:00Z" },
      { "type": "conversion", "name": "purchase", "value": 24.9, "currency": "EUR", "ts": "2026-09-11T10:05:00Z",
        "properties": { "sku": "AUTUMN20" } }
    ]
  }'
```

Events dated more than 5 minutes in the future or more than 30 days in the past are discarded per event; the batch is still `202`.

### `POST /v1/claim-codes`

The deterministic iOS path (ADR-008, [§B.6.3](../zadanie.md#b63-deferred-deep-link--ios-bez-determinizmu-z-platformy)): exchanges a fresh click for a six-character, single-use code the interstitial shows and the user types into the app. Only the keyed hash is stored; TTL `Dle:Attribution:ClaimCode:TtlMinutes` (default 15). The edge calls it; an SDK normally does not.

## Control — management API (`/api/v1`)

Spec: [§B.7.3](../zadanie.md#b73-control-plane-api-apiv1). Authentication: `Authorization: Bearer <api_key>` (secret shown once at creation, stored as Argon2id — K5) or an OIDC bearer token carrying the `dle_tenant` and `dle_role` claims.

### Authentication and scopes

An API key has a `role` and optional `scopes`. Scopes observed in the implementation: `admin`, `links:read`, `links:write`, `domains:read`, `domains:write`, `apps:read`, `apps:write`, `analytics:read`, `keys:read`, `keys:write`, `tenants:read`, `tenants:write`. A valid credential without the needed scope gets `403 …/forbidden`; a missing, malformed or expired one gets `401 …/unauthorized`. Verification is constant-time (T-17) and rate-limited (10/min per IP + identifier).

### Endpoint map

| Area | Method | Path | Notes |
|---|---|---|---|
| Tenants | GET | `/tenants/me` | The caller's tenant |
| | GET / POST | `/tenants`, `/tenants/{id}` | Instance operators only — see [deploy/README — First credential](../../deploy/README.md#first-credential) |
| | PATCH / DELETE | `/tenants/{id}` | |
| API keys | GET / POST | `/api-keys` | `POST` returns the secret exactly once |
| | DELETE | `/api-keys/{id}` | Revoke |
| Domains | GET / POST | `/domains`, `/domains/{id}` | |
| | PATCH / DELETE | `/domains/{id}` | |
| | POST | `/domains/{id}/verify` | Runs the 8-point check ([domains.md](../self-hosting/domains.md)) |
| | GET | `/domains/{id}/verifications` | History, incl. the nightly runs |
| Apps | GET / POST | `/apps`, `/apps/{id}` | `POST` response carries `warnings` (e.g. a fingerprint that looks like a debug keystore) |
| | PATCH / DELETE | `/apps/{id}` | |
| | GET / POST | `/apps/{id}/sdk-keys` | |
| | DELETE | `/apps/{id}/sdk-keys/{key_id}` | |
| Links | GET / POST | `/links` | List with search, tags, cursor paging; create |
| | POST | `/links/bulk` | NDJSON stream, max 10 000 rows, 2 concurrent batches per tenant |
| | GET / PATCH / DELETE | `/links/{id}` | `PATCH` records a revision (up to 50 kept) |
| | POST | `/links/{id}/archive` | Off without removal |
| | GET | `/links/{id}/versions` | Revision history |
| | GET | `/links/{id}/simulate?ua=…&platform=…&country=…&language=…&os_version=…&app_version=…&channel=…&at=…` | What a client would get; no click |
| | POST | `/links/{id}/simulate` | Same, client described in the body |
| | GET / POST / PATCH / DELETE | `/links/templates`, `/links/templates/{id}` | Campaign templates that prefill new links |
| Analytics | GET | `/analytics/clicks`, `/analytics/installs` | Time series (`from`, `to`, `grain`, filters) |
| | GET | `/analytics/breakdown?by=…` | By platform, country, channel, campaign … |
| | GET | `/analytics/funnels` | click → install → conversion |
| | GET | `/analytics/attribution-quality` | The deterministic / probabilistic / unmatched split with confidence distribution |
| | GET | `/analytics/export` | CSV / NDJSON export |
| | GET | `/analytics/stream` | Server-sent events for the live dashboard |
| Exports | GET | `/exports/tenant` | Full tenant data export — the DORA art. 30 exit-plan feature (FR-249) |
| Webhooks | GET / POST | `/webhooks` | `POST` returns `secret` exactly once and `signing_key_id` |
| | DELETE | `/webhooks/{id}` | |
| | POST | `/webhooks/{id}/test` | Sends a signed `webhook.test` and reports the outcome |
| | GET | `/webhooks/deliveries` | Delivery log with attempt, status, `next_attempt_at` |
| Abuse | POST | `/abuse-reports` *(root path on the control host, anonymous, 5/h per IP)* | DSA art. 16 notice-and-action (FR-245) |
| | GET | `/abuse-reports` | Reports about the caller's links |
| | GET | `/admin/abuse-reports`, POST `/admin/abuse-reports/{id}/decision` | Instance triage |
| | POST | `/admin/links/{link_id}/quarantine`, `…/quarantine/release` | `410` with an explanation page, not deletion ([§E.3](../zadanie.md#e3-ochrana-proti-zneužitiu-redirektora)) |
| Keys | GET | `/.well-known/jwks.json` *(root path, anonymous)* | Public signing keys — `kid`, `alg`, for webhook `v2` verification |

### Examples

```bash
export DLE=https://go.example.com; export KEY=dle_…

# Create a link, safely retryable
curl -s -X POST $DLE/api/v1/links -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" \
  -H "Idempotency-Key: 2026-09-11-autumn-001" \
  -d '{"domain_id":"…","target_url":"https://www.example.com/promo/autumn","deeplink_path":"/promo/autumn",
       "routing_rules":[],"utm":{"utm_campaign":"autumn26"},"tags":["autumn"],"is_active":true}'

# Routing rules: iOS below 17 goes to the web, everyone else to the app
curl -s -X PATCH $DLE/api/v1/links/<ID> -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" \
  -d '{"routing_rules":[
        {"when":{"platform":"ios","os_version":{"lt":"17"}},"then":{"action":"web","url":"https://www.example.com/legacy"}},
        {"when":{},"then":{"action":"app"}}
      ]}'

# Bulk, NDJSON: one CreateLinkRequest per line
printf '%s\n' '{"domain_id":"…","target_url":"https://example.com/a"}' '{"domain_id":"…","target_url":"https://example.com/b"}' \
  | curl -s -X POST $DLE/api/v1/links/bulk -H "Authorization: Bearer $KEY" -H "Content-Type: application/x-ndjson" \
         -H "Idempotency-Key: import-2026-09-11" --data-binary @-

# Simulate
curl -s "$DLE/api/v1/links/<ID>/simulate?platform=android&os_version=15&country=SK&channel=in_app_facebook" -H "Authorization: Bearer $KEY"

# Verify a domain, read the history
curl -s -X POST $DLE/api/v1/domains/<DOMAIN_ID>/verify -H "Authorization: Bearer $KEY"
curl -s $DLE/api/v1/domains/<DOMAIN_ID>/verifications -H "Authorization: Bearer $KEY"

# SDK key for an app
curl -s -X POST $DLE/api/v1/apps/<APP_ID>/sdk-keys -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" -d '{"name":"prod"}'

# Analytics
curl -s "$DLE/api/v1/analytics/clicks?from=2026-09-01T00:00:00Z&to=2026-09-11T00:00:00Z&grain=day" -H "Authorization: Bearer $KEY"
curl -s "$DLE/api/v1/analytics/attribution-quality?from=2026-09-01T00:00:00Z" -H "Authorization: Bearer $KEY"

# Webhook
curl -s -X POST $DLE/api/v1/webhooks -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" \
  -d '{"url":"https://hooks.example.com/dle","event_types":["attribution.created","link.quarantined"],"is_active":true}'
curl -s -X POST $DLE/api/v1/webhooks/<WH_ID>/test -H "Authorization: Bearer $KEY"

# Public abuse report (no auth)
curl -s -X POST $DLE/abuse-reports -H "Content-Type: application/json" \
  -d '{"url":"https://go.example.com/aB3xK9pQ","reason":"phishing","details":"…","reporter_email":"…"}'

# JWKS
curl -s $DLE/.well-known/jwks.json
```

The request-body field lists are the `Create*Request` records in `src/Dle.Domain/Contracts/*.cs`, serialised `snake_case`; `/scalar/v1` renders them with descriptions.

## Errors — RFC 9457 Problem Details

Every error is `application/problem+json` with `type`, `title`, `status`, `detail`, `instance`, plus extensions where noted. The `type` URI is an identifier — integrators branch on it — and is stable within a major version. Source of truth: `src/Dle.Domain/Contracts/ProblemCodes.cs`; the OpenAPI document enumerates the same list.

| `type` (prefix `https://docs.dle.dev/problems/`) | HTTP | When | Extensions |
|---|---|---|---|
| `validation-failed` | 400 | Body or query failed validation | `errors[]` |
| `slug-taken` | 409 | Slug already used on that domain | |
| `slug-invalid` | 400 | Syntactically invalid or reserved slug (`admin`, `api`, `.well-known` …). NFKC-normalised before the check, so homoglyphs are refused | |
| `unsafe-target` | 422 | `target_url` failed the safety policy — scheme not `http(s)`, resolves to a private / link-local address, or on a blocklist ([§E.3](../zadanie.md#e3-ochrana-proti-zneužitiu-redirektora), T-01, T-02) | |
| `rate-limited` | 429 | A limit in the table below | `Retry-After` header |
| `missing-default-rule` | 400 | Rule set has no catch-all rule (TC-105) | |
| `invalid-routing-rules` | 400 | Rule set otherwise invalid (depth, size, unknown operator …) | `errors[]` |
| `domain-not-verified` | 409 | Operation needs a verified domain (FR-143) | |
| `domain-taken` | 409 | Host already registered, possibly by another tenant | |
| `idempotency-conflict` | 409 | See below | |
| `unauthorized` | 401 | Credential missing, malformed, expired | |
| `forbidden` | 403 | Valid credential, insufficient scope or wrong tenant | |
| `claim-code-invalid` | 400 | Unknown, consumed or expired claim code (TC-148) | |
| `link-quarantined` | 410 | Link is under an abuse quarantine (TC-103) | |
| `dependency-unavailable` | 503 | PostgreSQL / cache unavailable; retry with backoff | `Retry-After` |
| `write-conflict` | 409 | The link changed between your read and your write (another edit, or an abuse quarantine); read it again and retry | |
| `link-too-large` | 422 | The link's resolve-time fields (target URL, expired URL, title, UTM set, rules, Open Graph) are together larger than the covering resolve index holds (about 2.7 kB after compression); shorten them | |

```json
{
  "type": "https://docs.dle.dev/problems/validation-failed",
  "title": "The request failed validation.",
  "status": 400,
  "detail": "target_url must be an absolute http or https URL.",
  "instance": "/api/v1/links",
  "errors": [ { "path": "target_url", "message": "must be an absolute http or https URL" } ]
}
```

## `Idempotency-Key`

Writes under `/api/v1` accept an `Idempotency-Key` header (any string ≤ 255 characters, unique per logical operation). The header is honoured, not required.

| Situation | Result |
|---|---|
| First request with the key | Executed; the response (status < 500) is stored for `Dle:Persistence:IdempotencyRetentionHours` (default 24 h) |
| Same key, same method + route + body | The stored response is replayed — nothing runs again |
| Same key, **different** body, or the same key on a different endpoint | `409 idempotency-conflict` — two different requests sharing a key is a client bug, and replaying the first answer would hide it |
| Same key while the first request is still running | `409 idempotency-conflict` with a "still being handled" detail — retry shortly |
| First request failed with a 5xx (`dependency-unavailable`) | The reservation is released; the next attempt with the same key runs normally |

Bulk imports reserve their key for the whole stream and replay the batch summary on retry.

## Rate limits and quotas

Defaults from [§E.9](../zadanie.md#e9-rate-limity-a-kvóty--konkrétne-hodnoty); every value is configurable under `Dle:RateLimits:*` ([configuration.md](../self-hosting/configuration.md)). Exceeding a limit yields `429 rate-limited` with `Retry-After`.

| Endpoint | Algorithm | Key | Default | On excess |
|---|---|---|---|---|
| `GET /{slug}` — successful | sliding window | IP /24 (v4) or /48 (v6) | 600 / min | 429 + `Retry-After` |
| `GET /{slug}` — **404 responses** | token bucket | IP /24 | **20 / min**, burst 40 | 429, then the prefix is shadow-banned for 15 min (404 without a DB query) |
| `POST /v1/resolve` | fixed window | `install_id` | 5 / h | 429 — a legitimate SDK calls it once per install |
| `POST /v1/events` | token bucket | `install_id` | 60 / min, burst 120 | 429; the SDKs back off exponentially |
| `POST /api/v1/links` | concurrency + sliding window | API key | 60 / min (new tenant: 10 / min for the first 7 days) | 429 |
| `POST /api/v1/links/bulk` | concurrency | tenant | 2 concurrent batches, ≤ 10 000 rows each | 429 |
| `GET /{slug}/qr` | sliding window | IP | 30 / min | 429 |
| `POST /abuse-reports` | fixed window (+ captcha where configured) | IP | 5 / h | 429 |
| Authentication (API key, claim code) | token bucket | IP + identifier | 10 / min | 429, constant-time verification |
| `/api/v1` reads / writes (general) | sliding window | API key | 600 / min read, 120 / min write | 429 |

The 404 budget is the important one: it is the primary defence against slug enumeration (T-07) and is kept **separate** from the success budget so that a viral campaign cannot exhaust the anti-enumeration protection.

Quotas (active links, domains, webhooks per tenant, click-stream retention) are soft limits meant for a hosted variant; exceeding one blocks creation, never resolution.
