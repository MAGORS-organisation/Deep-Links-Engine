# Quick start — the first 30 minutes

**What this is:** the walkthrough from an empty box to a short link that opens your app, in the order the pieces depend on each other, with the places it usually goes wrong. Today it cannot be finished: step 2 has no working path to a first credential, and step 6 says which expectations the engine does not meet yet.
**Who it is for:** the person doing the first install — an engineer or a technical operator — and the independent person who re-runs it on a clean machine before a release ([§D.7 item 6](../zadanie.md#d7-akceptačné-kritériá-pre-release)).

> Status honesty: the .NET services, the web SDK and the admin console build and their tests pass. CI applies the EF Core migration against PostgreSQL 18 and starts the compose stack — with the development override, against an empty database — for a ZAP scan of the edge; Caddy, TLS and `/.well-known` are not exercised there, and the mobile SDKs have not run on a real device ([README — Where this stands](../../README.md#where-this-stands), [Known gaps](../../README.md#known-gaps)). If step 1 fails, that is the first thing to report.

## Before you start

| You need | Why |
|---|---|
| A Linux host with Docker Engine 24+ and the compose plugin | Profile A runs everything in one `docker compose -f docker-compose.yml up` (NFR-09) |
| A DNS name pointing at the host, ports 80/443 open (TCP **and** UDP for HTTP/3) | Caddy needs it for TLS; Apple and Google fetch the association files over it |
| An OIDC provider (Keycloak, Entra ID, Auth0, Zitadel …) | Meant to issue the first credential — there is no seeded API key — but that path does not work today; see step 2 and [deploy/README — First credential](../../deploy/README.md#first-credential) |
| The iOS Team ID and bundle ID, or the Android package name and the **Play App Signing** SHA-256 fingerprint | For step 4 |

The 30-minute budget assumes the DNS record already exists and the OIDC client is already created. It cannot be met today: the walkthrough stops at step 2.

## Step 1 — bring the stack up (≈ 5 min)

Follow [deploy/README.md — Profile A](../../deploy/README.md#profile-a--one-server-five-commands) exactly; do not duplicate it here. The short form:

```bash
git clone https://github.com/MAGORS-organisation/Deep-Links-Engine.git
cd Deep-Links-Engine/deploy
cp .env.example .env      # DLE_DOMAIN, DLE_TLS, POSTGRES_PASSWORD, DLE_MASTER_SECRET (openssl rand -base64 48)
                          # DLE_OIDC_* and DLE_ALLOW_TENANT_SELF_SERVICE do not help yet — see step 2
docker compose -f docker-compose.yml up -d --build   # no images are published yet; this builds them from source
docker compose -f docker-compose.yml ps -a           # -a: plain `ps` hides the exited migrate container
```

Always name the file: a plain `docker compose …` in `deploy/` also loads `docker-compose.override.yml`, the development profile (Caddy's internal CA, `Development` mode, plain-HTTP `PublicScheme`, tenant self-service on, one edge replica).

You are done with this step when every service is `healthy`, `migrate` is `exited (0)`, and:

```bash
curl -sI https://go.example.com/.well-known/apple-app-site-association | head -1   # HTTP/2 404 until an iOS app is registered (step 4), then 200
curl -sI http://go.example.com/.well-known/apple-app-site-association  | head -1   # HTTP/1.1 404, later 200 — never 301, never 308
```

The `404` is the edge's deliberate answer for a host with no iOS application (Apple retries a 404; it would cache an empty document), and it already proves that Caddy, TLS and the edge are wired. The second line matters more than it looks: a redirect on `/.well-known` breaks Universal Links and App Links silently ([§A.2.1](../zadanie.md#a21-apple-universal-links), [deploy/README — Never redirect /.well-known](../../deploy/README.md#never-redirect-well-known)).

**Where it goes wrong**

| Symptom | Cause | Fix |
|---|---|---|
| `migrate` exits non-zero | `POSTGRES_PASSWORD` changed after the volume was initialised, or the migration failed on your PostgreSQL (CI applies it against PostgreSQL 18 only) | Check `docker compose -f docker-compose.yml logs migrate`; for a fresh box, `docker compose -f docker-compose.yml down -v` and start again with the final password; otherwise report the SQL error verbatim |
| `dle-control` restarts in a loop | `DLE_MASTER_SECRET` shorter than 32 characters — options validation fails at start-up by design | Generate a longer one. Changing it later changes the slug permutation and every key derived from it ([backup-restore.md](backup-restore.md)), so set it once |
| Caddy has no certificate | Port 80 unreachable for the ACME challenge, or `DLE_TLS` not an e-mail address | Open the port or use `DLE_TLS=internal` for a lab |
| Well-known answers `308` | Something in front of Caddy (CDN, WAF, a second proxy) forces HTTPS | Exempt `/.well-known/*` there |

## Step 2 — the first credential (no working path today)

There is currently **no working path to a first credential** on a fresh install, so a fresh install cannot be administered through the API or the console, and the walkthrough stops here ([README — Known gaps](../../README.md#known-gaps), [deploy/README — First credential](../../deploy/README.md#first-credential)):

- nothing is seeded, and there is no bootstrap key;
- the admin console at `https://go.example.com/admin/` has no OIDC sign-in — it accepts a pasted API key only;
- an OIDC bearer token authenticates but establishes no tenant: the control plane reads the tenant from a claim named `dle:tenant`, and the configured `Dle:Identity:Oidc:TenantClaim` (default `dle_tenant`) is never mapped to it. `DLE_ALLOW_TENANT_SELF_SERVICE=true` does not change that — self-service still needs a caller that carries a tenant.

Steps 3–6 assume an API key with role `admin` in `$DLE_KEY`. For reference, a caller that already holds one creates further keys like this (all wire JSON is `snake_case`; scopes are `links:write`, `domains:read`, … see [integration/api.md](../integration/api.md#authentication-and-scopes)):

```bash
export DLE=https://go.example.com
export DLE_KEY=dle_…      # an existing admin key of the tenant

curl -s -X POST $DLE/api/v1/api-keys \
  -H "Authorization: Bearer $DLE_KEY" -H "Content-Type: application/json" \
  -d '{"name":"ci","role":"admin","scopes":[]}'
# → {"id":"…","name":"ci","secret":"dle_…","prefix":"dle","role":"admin","expires_at":null}
# the secret is shown exactly once; it is stored as an Argon2id hash
```

A new tenant's `consent_mode` defaults to `aggregate_only`, the mode that needs no consent banner — IP addresses are hashed with a daily-rotated salt and never stored raw ([§E.6.2](../zadanie.md#e62-tri-režimy-prevádzky-produktová-funkcia-nie-prepínač-v-kóde), [compliance/privacy.md](../compliance/privacy.md)). In this mode the Play referrer carries no `dl_cid`, so Android deferred deep linking is off (step 6).

**Where it goes wrong**

| Symptom | Cause |
|---|---|
| An OIDC token is refused | Expected today, see above — adding `dle_tenant` / `dle_role` claims to the token does not help |
| `403` with `type: …/forbidden` on `POST /api/v1/tenants` | Self-service is off and the caller's tenant is not `Dle:Control:InstanceTenantId` — with neither set, tenant routes deny everyone, deliberately |
| `429` on the key endpoints | Authentication is rate-limited at 10/min per IP + identifier ([§E.9](../zadanie.md#e9-rate-limity-a-kvóty--konkrétne-hodnoty)) |

## Step 3 — register the link domain (≈ 3 min, plus propagation)

```bash
curl -s -X POST $DLE/api/v1/domains \
  -H "Authorization: Bearer $DLE_KEY" -H "Content-Type: application/json" \
  -d '{"host":"go.example.com","is_default":true}'
# → {"id":"<DOMAIN_ID>","host":"go.example.com","tls_status":"…","aasa_status":"…","assetlinks_status":"…", …}
export DOMAIN_ID=…
```

The domain can already serve links. The association files it serves are still empty of apps — that is step 4. Verification (`POST /api/v1/domains/{id}/verify`) is most useful **after** step 4; the full 8-point runbook is [domains.md](domains.md).

## Step 4 — register the app (≈ 5 min)

iOS (Team ID + bundle ID; Apple reads `TEAMID.bundle.id` from the AASA):

```bash
curl -s -X POST $DLE/api/v1/apps \
  -H "Authorization: Bearer $DLE_KEY" -H "Content-Type: application/json" \
  -d '{"platform":"ios","bundle_id":"com.example.app","team_id":"ABCDE12345",
       "store_id":"123456789","store_url":"https://apps.apple.com/app/id123456789",
       "cert_fingerprints":[],"domain_ids":["'$DOMAIN_ID'"]}'
```

Android (package name + the SHA-256 fingerprint **from Play Console → App signing**, not from your local keystore — [FR-144, §A.2.2](../zadanie.md#a22-android-app-links)):

```bash
curl -s -X POST $DLE/api/v1/apps \
  -H "Authorization: Bearer $DLE_KEY" -H "Content-Type: application/json" \
  -d '{"platform":"android","bundle_id":"com.example.app",
       "cert_fingerprints":["AA:BB:…:FF"],
       "store_url":"https://play.google.com/store/apps/details?id=com.example.app",
       "domain_ids":["'$DOMAIN_ID'"]}'
```

The response carries a `warnings` array — read it. Then run the verifier:

```bash
curl -s -X POST $DLE/api/v1/domains/$DOMAIN_ID/verify -H "Authorization: Bearer $DLE_KEY"
# → {"ok":true,"checks":[{"kind":"…","status":"…","codes":[…]}], "propagation_notice":"…"}
```

`propagation_notice` is not decoration: Apple's CDN pulls the file within about 24 hours and devices refresh roughly weekly; Android 15+ re-verifies within up to 7 days. A link created now opens the app on a device that has seen the new file, and falls back to the store or the web on one that has not ([§A.2.1](../zadanie.md#a21-apple-universal-links)).

For the app side (entitlement `applinks:go.example.com`, the Android intent filter, the SDK call that reports opens) see [integration/sdk-ios.md](../integration/sdk-ios.md) and [integration/sdk-android.md](../integration/sdk-android.md).

## Step 5 — create a link (≈ 2 min)

```bash
curl -s -X POST $DLE/api/v1/links \
  -H "Authorization: Bearer $DLE_KEY" -H "Content-Type: application/json" \
  -H "Idempotency-Key: quickstart-001" \
  -d '{"domain_id":"'$DOMAIN_ID'",
       "target_url":"https://www.example.com/promo/autumn",
       "deeplink_path":"/promo/autumn",
       "title":"Autumn promo",
       "utm":{"utm_source":"quickstart","utm_campaign":"autumn26"},
       "routing_rules":[{"id":"default","then":{"action":"web","url":"https://www.example.com/promo/autumn"}}],
       "tags":["quickstart"],
       "is_active":true}'
# → {"id":"…","slug":"aB3xK9pQ","short_url":"https://go.example.com/aB3xK9pQ","qr_url":"https://go.example.com/aB3xK9pQ/qr", …}
```

Leave `slug` out and an 8-character one is generated from a keyed permutation (ADR-007) — it is not guessable and not sequential. Supply your own for a vanity slug; `slug-taken` and `slug-invalid` are the two problem types you may get back. Routing rules are data, not code: first match wins and the rule **without** a `when` is the mandatory default — a rule set without one is refused with `missing-default-rule` (TC-105). The single `web` default rule above — which is also what a link created without `routing_rules` gets — makes this link web-only: every client, phones and the Instagram/Facebook in-app browsers included, gets a `302` to `target_url`; it never sends anyone to the store and never shows the interstitial. Store and interstitial behaviour needs an explicit `app_or_store` or `store_only` rule, and each such rule must carry its own `store_url` — the `store_url` registered with the app in step 4 is not used as a fallback. On Android that rule also needs a `referrer_template` carrying `dl_cid={click_id}`, which is filled in only under the consent conditions in step 6. The rule grammar is in [integration/api.md](../integration/api.md#examples).

## Step 6 — click it (≈ 5 min)

Before touching a phone, ask the engine what it *would* do — no click is recorded:

```bash
curl -s "$DLE/api/v1/links/<LINK_ID>/simulate?ua=Mozilla/5.0%20(iPhone;%20CPU%20iPhone%20OS%2018_0%20like%20Mac%20OS%20X)&country=SK" \
  -H "Authorization: Bearer $DLE_KEY"
curl -sI "https://go.example.com/aB3xK9pQ?_dl=preview"    # FR-166: full pipeline, no click event
```

Then the real thing. The third column is what the engine does today where that differs from the expectation ([README — Known gaps](../../README.md#known-gaps)):

| Client | Expected | Today |
|---|---|---|
| `curl -sI https://go.example.com/aB3xK9pQ` | `302` to `target_url` (desktop class, no app) — **never `301`** (ADR-009) | As expected |
| `curl -s -A facebookexternalhit/1.1 https://go.example.com/aB3xK9pQ` | `200` HTML with `og:*` tags, no redirect ([§A.2.6](../zadanie.md#a26-in-app-prehliadače-a-crawlery)) | As expected |
| `curl -sI https://go.example.com/aB3xK9pQ/qr?format=svg&size=512` | `200 image/svg+xml` | As expected |
| Phone, app installed, tap in Safari/Chrome | App opens on `/promo/autumn`; the SDK reports `link_open` | The app opens and can report the open, but it receives only the short URL (`/aB3xK9pQ`): nothing expands a slug into its `deeplink_path`, so it cannot land on `/promo/autumn` unless it parses a human-readable slug itself |
| Phone, app not installed | Interstitial with a real button → store → after install `POST /v1/resolve` returns `match_type: install_referrer` (Android) or `claim_code` / `login` / `none` (iOS — [§B.6.3](../zadanie.md#b63-deferred-deep-link--ios-bez-determinizmu-z-platformy)) | The step-5 link sends the phone to `target_url` — no interstitial, no store. With an `app_or_store` rule, Android gets `install_referrer` only when the tenant's `consent_mode` is `full` **and** the short URL carried `dl_consent=all` (or `gdpr=0`), otherwise `none` with `consent_missing`; iOS always gets `none`, because the edge never issues a claim code and login matching reads a click field nothing writes |
| Phone, tap from **inside Instagram** | Interstitial with the button; the button opens the app, the page load does not | The step-5 link redirects to `target_url`, no interstitial. With an `app_or_store` rule the interstitial appears, but its button is a custom-scheme URL (the app's `custom_scheme`), never a Universal Link, and it is missing when the app has no custom scheme |

Check the dashboard (`/admin/`) or `GET /api/v1/analytics/clicks?…` — the click is there with `is_bot=false`, and the crawler hit with `is_bot=true`.

**Where it goes wrong**

| Symptom | Most likely cause | Where to look |
|---|---|---|
| App Links open a chooser or the browser on Android | Fingerprint from the local keystore instead of Play App Signing; or the device has not re-verified yet | [domains.md](domains.md), `adb shell pm get-app-links com.example.app` |
| Universal Link opens Safari instead of the app | AASA fetched before the app was registered (wait for the CDN), redirect on `/.well-known`, or a `?mode=developer` entitlement on a store build | [troubleshooting.md](troubleshooting.md) |
| Slack/Facebook show an empty preview | Crawler received a redirect instead of the OG page — usually a proxy in front rewriting the response | [troubleshooting.md](troubleshooting.md#3-empty-previews-in-slack-facebook-or-imessage) |
| `503` from the edge | PostgreSQL is down **and** the link is not in cache. By design: the edge serves from cache during a database outage and refuses rather than guesses when it has nothing (NFR-06) | [troubleshooting.md](troubleshooting.md#7-503-from-the-edge-while-the-database-is-down) |
| `429` while testing in a loop | 600/min per /24 on successful resolves; **20/min on 404s** — mistyped slugs shadow-ban your IP for 15 min ([§E.9](../zadanie.md#e9-rate-limity-a-kvóty--konkrétne-hodnoty)) | Wait, or test from another prefix |

## What to do next

1. Keep self-service off, narrow the API key's scopes, set up a second key for CI.
2. Read [configuration.md](configuration.md) — in particular `Dle:Privacy:*` and `Dle:Edge:Network:*`.
3. Register a webhook for `attribution.created` and verify its signature ([integration/webhooks.md](../integration/webhooks.md)).
4. Schedule the nightly domain verification (`.github/scripts/verify-domains.py` or the built-in worker) — a release needs three consecutive green nights ([§D.7 item 7](../zadanie.md#d7-akceptačné-kritériá-pre-release)).
5. Put the [backup-restore.md](backup-restore.md) drill in the calendar before you send the first campaign.
