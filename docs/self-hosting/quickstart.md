# Quick start — the first 30 minutes

**What this is:** the walkthrough from an empty box to a short link that opens your app, in the order the pieces depend on each other, with the places it usually goes wrong.
**Who it is for:** the person doing the first install — an engineer or a technical operator — and the independent person who re-runs it on a clean machine before a release ([§D.7 item 6](../zadanie.md#d7-akceptačné-kritériá-pre-release)).

> Status honesty: the .NET services, the web SDK and the admin console build and their tests pass. The compose stack, the EF Core migration and the mobile SDKs have **not** been exercised against a live PostgreSQL or a real device on the build machine yet ([README — Where this stands](../../README.md#where-this-stands)). If step 1 fails, that is the first thing to report.

## Before you start

| You need | Why |
|---|---|
| A Linux host with Docker Engine 24+ and the compose plugin | Profile A runs everything in one `docker compose up` (NFR-09) |
| A DNS name pointing at the host, ports 80/443 open (TCP **and** UDP for HTTP/3) | Caddy needs it for TLS; Apple and Google fetch the association files over it |
| An OIDC provider (Keycloak, Entra ID, Auth0, Zitadel …) | There is no seeded API key — the first credential is a human signed in through OIDC ([deploy/README — First credential](../../deploy/README.md#first-credential)) |
| The iOS Team ID and bundle ID, or the Android package name and the **Play App Signing** SHA-256 fingerprint | For step 4 |

The 30-minute budget assumes the DNS record already exists and the OIDC client is already created.

## Step 1 — bring the stack up (≈ 5 min)

Follow [deploy/README.md — Profile A](../../deploy/README.md#profile-a--one-server-five-commands) exactly; do not duplicate it here. The short form:

```bash
git clone https://github.com/MAGORS-organisation/Deep-Links-Engine.git
cd Deep-Links-Engine/deploy
cp .env.example .env      # DLE_DOMAIN, DLE_TLS, POSTGRES_PASSWORD, DLE_MASTER_SECRET (openssl rand -base64 48)
                          # + DLE_OIDC_AUTHORITY / DLE_OIDC_CLIENT_ID / DLE_OIDC_AUDIENCE
                          # + DLE_ALLOW_TENANT_SELF_SERVICE=true for the first run
docker compose -f docker-compose.yml up -d
docker compose -f docker-compose.yml ps
```

You are done with this step when every service is `healthy`, `migrate` is `exited (0)`, and:

```bash
curl -sI https://go.example.com/.well-known/apple-app-site-association | head -1   # HTTP/2 200
curl -sI http://go.example.com/.well-known/apple-app-site-association  | head -1   # HTTP/1.1 200 — not 301, not 308
```

The second line matters more than it looks: a redirect on `/.well-known` breaks Universal Links and App Links silently ([§A.2.1](../zadanie.md#a21-apple-universal-links), [deploy/README — Never redirect /.well-known](../../deploy/README.md#never-redirect-well-known)).

**Where it goes wrong**

| Symptom | Cause | Fix |
|---|---|---|
| `migrate` exits non-zero | `POSTGRES_PASSWORD` changed after the volume was initialised, or the migration hit a real PostgreSQL for the first time (it has never been applied on the build machine) | Check `docker compose logs migrate`; for a fresh box, `docker compose down -v` and start again with the final password; otherwise report the SQL error verbatim |
| `dle-control` restarts in a loop | `DLE_MASTER_SECRET` shorter than 32 characters — options validation fails at start-up by design | Generate a longer one. Changing it later changes every slug, so set it once |
| Caddy has no certificate | Port 80 unreachable for the ACME challenge, or `DLE_TLS` not an e-mail address | Open the port or use `DLE_TLS=internal` for a lab |
| Well-known answers `308` | Something in front of Caddy (CDN, WAF, a second proxy) forces HTTPS | Exempt `/.well-known/*` there |

## Step 2 — create a tenant and an API key (≈ 5 min)

Sign in to `https://go.example.com/admin/`. Because `DLE_ALLOW_TENANT_SELF_SERVICE=true`, the signed-in user may create the first tenant. Create it, then create an API key for automation (role `admin` is fine for the walkthrough; narrow it later — scopes are `links:write`, `domains:read`, … see [integration/api.md](../integration/api.md#authentication-and-scopes)).

The equivalent with an OIDC bearer token, if you prefer the terminal (all wire JSON is `snake_case`):

```bash
export DLE=https://go.example.com
export OIDC_TOKEN=…   # a token from your provider carrying dle_tenant / dle_role claims

curl -s -X POST $DLE/api/v1/tenants \
  -H "Authorization: Bearer $OIDC_TOKEN" -H "Content-Type: application/json" \
  -d '{"slug":"acme","name":"ACME Ltd","consent_mode":"aggregate_only"}'

curl -s -X POST $DLE/api/v1/api-keys \
  -H "Authorization: Bearer $OIDC_TOKEN" -H "Content-Type: application/json" \
  -d '{"name":"quickstart","role":"admin","scopes":[]}'
# → {"id":"…","name":"quickstart","secret":"dle_…","prefix":"dle","role":"admin","expires_at":null}
export DLE_KEY=dle_…      # the secret is shown exactly once; it is stored as an Argon2id hash
```

`consent_mode: aggregate_only` is the default and the mode that needs no consent banner — IP addresses are hashed with a daily-rotated salt and never stored raw ([§E.6.2](../zadanie.md#e62-tri-režimy-prevádzky-produktová-funkcia-nie-prepínač-v-kóde), [compliance/privacy.md](../compliance/privacy.md)).

Then set `DLE_ALLOW_TENANT_SELF_SERVICE=false` again unless you want every signed-in user to be able to create tenants.

**Where it goes wrong**

| Symptom | Cause |
|---|---|
| `401` with `type: …/unauthorized` | The OIDC token lacks the `dle_tenant` / `dle_role` claims (names configurable under `Dle:Identity:Oidc`) |
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

Leave `slug` out and an 8-character one is generated from a keyed permutation (ADR-007) — it is not guessable and not sequential. Supply your own for a vanity slug; `slug-taken` and `slug-invalid` are the two problem types you may get back. Routing rules are data, not code: first match wins and the rule **without** a `when` is the mandatory default — a rule set without one is refused with `missing-default-rule` (TC-105). The single default rule above sends everyone to the web page; the platform-aware version (`app_or_store` with a `store_url` and, on Android, a `referrer_template` carrying `dl_cid={click_id}`) is in [integration/api.md](../integration/api.md#examples).

## Step 6 — click it (≈ 5 min)

Before touching a phone, ask the engine what it *would* do — no click is recorded:

```bash
curl -s "$DLE/api/v1/links/<LINK_ID>/simulate?ua=Mozilla/5.0%20(iPhone;%20CPU%20iPhone%20OS%2018_0%20like%20Mac%20OS%20X)&country=SK" \
  -H "Authorization: Bearer $DLE_KEY"
curl -sI "https://go.example.com/aB3xK9pQ?_dl=preview"    # FR-166: full pipeline, no click event
```

Then the real thing:

| Client | Expected |
|---|---|
| `curl -sI https://go.example.com/aB3xK9pQ` | `302` to `target_url` (desktop class, no app) — **never `301`** (ADR-009) |
| `curl -s -A facebookexternalhit/1.1 https://go.example.com/aB3xK9pQ` | `200` HTML with `og:*` tags, no redirect ([§A.2.6](../zadanie.md#a26-in-app-prehliadače-a-crawlery)) |
| `curl -sI https://go.example.com/aB3xK9pQ/qr?format=svg&size=512` | `200 image/svg+xml` |
| Phone, app installed, tap in Safari/Chrome | App opens on `/promo/autumn`; the SDK reports `link_open` |
| Phone, app not installed | Interstitial with a real button → store → after install `POST /v1/resolve` returns `match_type: install_referrer` (Android) or `claim_code` / `login` / `none` (iOS — [§B.6.3](../zadanie.md#b63-deferred-deep-link--ios-bez-determinizmu-z-platformy)) |
| Phone, tap from **inside Instagram** | Interstitial with the button; the button opens the app, the page load does not |

Check the dashboard (`/admin/`) or `GET /api/v1/analytics/clicks?…` — the click is there with `is_bot=false`, and the crawler hit with `is_bot=true`.

**Where it goes wrong**

| Symptom | Most likely cause | Where to look |
|---|---|---|
| App Links open a chooser or the browser on Android | Fingerprint from the local keystore instead of Play App Signing; or the device has not re-verified yet | [domains.md](domains.md), `adb shell pm get-app-links com.example.app` |
| Universal Link opens Safari instead of the app | AASA fetched before the app was registered (wait for the CDN), redirect on `/.well-known`, or a `?mode=developer` entitlement on a store build | [troubleshooting.md](troubleshooting.md) |
| Slack/Facebook show an empty preview | Crawler received a redirect instead of the OG page — usually a proxy in front rewriting the response | [troubleshooting.md](troubleshooting.md#empty-previews-in-slack-facebook-or-imessage) |
| `503` from the edge | PostgreSQL is down **and** the link is not in cache. By design: the edge serves from cache during a database outage and refuses rather than guesses when it has nothing (NFR-06) | [troubleshooting.md](troubleshooting.md#503-from-the-edge-while-the-database-is-down) |
| `429` while testing in a loop | 600/min per /24 on successful resolves; **20/min on 404s** — mistyped slugs shadow-ban your IP for 15 min ([§E.9](../zadanie.md#e9-rate-limity-a-kvóty--konkrétne-hodnoty)) | Wait, or test from another prefix |

## What to do next

1. Turn self-service off, narrow the API key's scopes, set up a second key for CI.
2. Read [configuration.md](configuration.md) — in particular `Dle:Privacy:*` and `Dle:Edge:Network:*`.
3. Register a webhook for `attribution.created` and verify its signature ([integration/webhooks.md](../integration/webhooks.md)).
4. Schedule the nightly domain verification (`.github/scripts/verify-domains.py` or the built-in worker) — a release needs three consecutive green nights ([§D.7 item 7](../zadanie.md#d7-akceptačné-kritériá-pre-release)).
5. Put the [backup-restore.md](backup-restore.md) drill in the calendar before you send the first campaign.
