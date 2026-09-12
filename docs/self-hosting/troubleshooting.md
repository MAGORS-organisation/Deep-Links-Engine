# Troubleshooting

**What this is:** the seven failures that generate most tickets for a deep-linking engine, each with the cause, the one command that proves it, and the fix.
**Who it is for:** whoever is on call for the instance, and the app developer who is sure "the link is broken" when the platform is doing what it documents.

Start with the verifier — it catches four of the seven: `POST /api/v1/domains/{id}/verify` ([domains.md](domains.md)). Then the table.

| Symptom | Section |
|---|---|
| App Links work in the debug build, not in production | [1](#1-app-links-work-in-debug-not-in-production) |
| Universal Links stopped, or never started | [2](#2-aasa-served-with-a-redirect) |
| Empty preview in Slack, Facebook, iMessage | [3](#3-empty-previews-in-slack-facebook-or-imessage) |
| Universal Link does not open the app from Instagram / Facebook | [4](#4-universal-link-not-opening-from-instagram) |
| Android install has an empty referrer | [5](#5-empty-install-referrer) |
| Attribution does not match, or matches with `none` | [6](#6-attribution-not-matching) |
| Edge returns `503` | [7](#7-503-from-the-edge-while-the-database-is-down) |

## 1. App Links work in debug, not in production

**Cause.** The debug build is signed with your local keystore; the production build on a real device is signed by **Google's app-signing key** (Play App Signing, default since 2021). `assetlinks.json` carries the debug fingerprint, so the device's verification fails and Android shows a chooser or opens the browser ([§A.2.2](../zadanie.md#a22-android-app-links), FR-144).

**Prove it.**

```bash
adb shell pm get-app-links com.example.app        # "go.example.com: none" or "legacy_failure" on the Play build, "verified" on the debug build
curl -s https://go.example.com/.well-known/assetlinks.json | jq '.[].target.sha256_cert_fingerprints'
# compare with Play Console → Setup → App signing → App signing key certificate → SHA-256
```

**Fix.** Add the Play fingerprint to the app (`PATCH /api/v1/apps/{id}` with both fingerprints in `cert_fingerprints`), re-verify, then on the test device `adb shell pm verify-app-links --re-verify com.example.app`. Other users' devices pick it up within up to 7 days (Android 15+) or at the next app update.

## 2. AASA served with a redirect

**Cause.** Something in front of the edge answered `301`/`308` for `/.well-known/apple-app-site-association` — an HTTP→HTTPS redirect, a trailing slash, a `www` canonicalisation, a CDN "pretty URL" rewrite. Apple's CDN and Google's verifier refuse any `3xx`; the failure is silent and the recovery takes up to 7 days ([§A.2.1](../zadanie.md#a21-apple-universal-links), [deploy/README — Never redirect /.well-known](../../deploy/README.md#never-redirect-well-known)).

**Prove it.**

```bash
curl -s -o /dev/null -w '%{http_code} %{content_type} %{redirect_url}\n' http://go.example.com/.well-known/apple-app-site-association
curl -s -o /dev/null -w '%{http_code} %{content_type} %{redirect_url}\n' https://go.example.com/.well-known/apple-app-site-association
# both: 200 application/json and an empty redirect_url. Also from outside your network — Apple fetches from its CDN.
```

**Fix.** Exempt `/.well-known/*` at the layer that redirects (Caddy: no `redir`/`uri` before the well-known handlers; Ingress: the separate `<release>-well-known` Ingress; CDN/WAF: an exemption rule). The engine itself never redirects these paths. iOS 18+: Settings → Developer → Universal Links → Diagnostics shows the fetch result; on a fresh install the device fetches immediately, otherwise weekly.

## 3. Empty previews in Slack, Facebook or iMessage

**Cause.** The crawler (`Slackbot-LinkExpanding`, `facebookexternalhit`, `Twitterbot`, `LinkedInBot`, `Discordbot`, `WhatsApp`, Apple's `facebookexternalhit`-like iMessage agent) received a `302` instead of a `200` HTML page with Open Graph tags — the engine branches on client type and serves crawlers the OG page ([§A.2.6](../zadanie.md#a26-in-app-prehliadače-a-crawlery)), so a redirect means a proxy in front rewrote the response, `BotDetection:ReverseDnsVerify` timed out and the client was classified as a browser, or the link has no `og` metadata and the domain no `default_og`.

**Prove it.**

```bash
curl -s -A "Slackbot-LinkExpanding 1.0 (+https://api.slack.com/robots)" https://go.example.com/aB3xK9pQ | grep -o '<meta property="og:[a-z:]*" content="[^"]*"'
curl -sI -A "facebookexternalhit/1.1" https://go.example.com/aB3xK9pQ | head -1        # HTTP/2 200, not 302
curl -s "https://go.example.com/api/v1/links/<ID>/simulate?ua=Slackbot-LinkExpanding%201.0" -H "Authorization: Bearer …"   # decision: og_page
```

**Fix.** Set `og` on the link (`title`, `description`, `image`) or `default_og` on the domain; if the response is a `302` for the crawler UA, look at the proxy. Facebook's debugger (`developers.facebook.com/tools/debug/`) and Slack re-fetch only after the cache expires — use "scrape again". Preview images must be reachable by the crawler without authentication.

## 4. Universal Link not opening from Instagram

**Cause.** Not a bug. In-app browsers (Instagram, Facebook, TikTok) do **not** trigger the OS-level Universal Link interception on page load; only a genuine tap on an `<a>` element does, and `window.location.href` from JavaScript is not a user gesture on iOS ([§A.2.6](../zadanie.md#a26-in-app-prehliadače-a-crawlery)). This is why the engine serves an interstitial with a real button to the `in_app_*` channels instead of redirecting.

**Prove it.** Simulate with the Instagram UA and check the decision is `interstitial`:

```bash
curl -s "https://go.example.com/api/v1/links/<ID>/simulate?platform=ios&channel=in_app_instagram" -H "Authorization: Bearer …"
```

On a device: tap the link in an Instagram post — the interstitial page must appear with an "Open in *App*" button; tapping the button opens the app. If the interstitial appears but the button does not open the app, it is a Universal Link problem (sections 1–2), not a webview problem.

**Fix.** Keep `Dle:Edge:Interstitial:Enabled=true`; do not add a routing rule that forces `web` for `in_app_*` channels unless you mean it. TikTok strips some query parameters and Facebook appends `fbclid` — the engine tolerates both; keep your own parameters in the link's `utm`, not in the shared URL.

## 5. Empty install referrer

**Cause.** One of: the user installed from a Play URL that did not carry `&referrer=`; the routing rule's `then.referrer_template` is missing, so no `dl_cid={click_id}` was put on the store URL; the install came from a source other than Play (side-load, other store, pre-install); the app read the referrer after its 90-day availability, or from a second install on the same device where Play no longer reports it; the Android SDK is queried before the Install Referrer client connected ([§A.2.4](../zadanie.md#a24-odovzdávanie-kontextu-cez-inštaláciu)).

**Prove it.**

```bash
# What the store URL looks like for an Android client — the referrer must be in it
curl -s "https://go.example.com/api/v1/links/<ID>/simulate?platform=android&os_version=15" -H "Authorization: Bearer …" | jq '.store_url'
# On the device, replay what Play would deliver
adb shell am broadcast -a com.android.vending.INSTALL_REFERRER -n com.example.app/<ReferrerReceiver> --es referrer "dl_cid%3DaB3xK9pQ%26utm_source%3Dtest"
```

**Fix.** Give the Android rule a `referrer_template` (`dl_cid={click_id}&utm_source={utm_source}&utm_campaign={utm_campaign}`, kept under ~500 characters — Google publishes no maximum), make sure the SDK's `resolve()` runs after the referrer client reports `OK`, and treat `none` honestly: a side-loaded install has no referrer and the engine will not invent one.

## 6. Attribution not matching

Work through the deterministic paths in the order the engine evaluates them ([§B.6.3](../zadanie.md#b63-deferred-deep-link--ios-bez-determinizmu-z-platformy)):

| Check | Command / place | Expect |
|---|---|---|
| Did the click get recorded at all? | `GET /api/v1/analytics/clicks?…` for the minute of the click; `is_bot` | A human click. A `?_dl=preview` hit records nothing by design |
| Was the consent mode `full`? | `GET /api/v1/tenants/me` → `consent_mode`; domain `consent_mode_override` | `full`. In `aggregate_only` there is **no** `click_id` ↔ install binding — that is the mode's point ([compliance/privacy.md](../compliance/privacy.md)) |
| Did the SDK send consent? | The `POST /v1/resolve` body: `consent.attribution=true` with `ts` | Without it signals are dropped before storage, not stored and ignored later |
| Android: referrer present and untampered? | Section 5; `match_type` in the response | `install_referrer`, `confidence: 1.0`. A modified `dl_cid` fails the HMAC (T-05) and yields `none` |
| iOS: claim code entered? | `match_type: claim_code` | The code is single-use and expires after `Dle:Attribution:ClaimCode:TtlMinutes` (15); `claim-code-invalid` afterwards |
| iOS: login reconciliation? | `login_key` on `/v1/resolve`, `match_type: login` | Requires the web side to have sent the same key |
| Was `/v1/resolve` called within 5/h per `install_id`? | `429 rate-limited` in the SDK log | The SDK should call it once per install |
| Probabilistic expected? | `Dle:Attribution:Probabilistic:Enabled` and `Strategies` | Off by default; on, it needs consent and a click within **60 minutes** — an install a day after the click is `none` on purpose ([§A.2.5](../zadanie.md#a25-presnosť-probabilistického-párovania--čísla)) |
| Direct open counted? | `POST /v1/events` with `type: link_open` | An installed app opens **without** any HTTP request to the engine; if the SDK does not report it, the click stream is systematically under-counted on your best campaigns ([§B.6.4](../zadanie.md#b64-priame-otvorenie-najčastejší-prípad-ktorý-sa-zabúda)) |

`GET /api/v1/analytics/attribution-quality` shows the deterministic / probabilistic / unmatched split; a sudden rise in `none` after an app release usually means the SDK call order changed.

## 7. `503` from the edge while the database is down

**Cause — by design.** With PostgreSQL unreachable the edge keeps serving every link that is in its L1 (in-process, 30 s) or L2 (Valkey, 10 min) cache and answers `503 dependency-unavailable` with `Retry-After` for anything it has never seen or whose cache entry expired (NFR-06). It does not guess, it does not fall back to a generic page (which would be a `200` that lies), and it never serves a cached `404` for a link that might exist (`Cache:NegativeSeconds` is 15 s).

**Prove it.**

```bash
curl -s  https://go.example.com/readyz                        # stays 200 but the body says "database: Degraded" while the DB is down (cached links keep resolving, so the edge stays in rotation); the control plane's /readyz answers 503
curl -sI https://go.example.com/aB3xK9pQ                       # 302 if cached, 503 if not
docker compose -f docker-compose.yml ps postgres               # or kubectl get pods -n dle
```

Metrics: `dle_resolve_total{outcome="dependency_unavailable"}` climbs, `dle_cache_hit_ratio` stays high for warm links. Click events written during the outage are buffered in the bounded channel; when it fills they are dropped and `dle_click_events_dropped_total` increments — that is the alert that tells you how much analytics you lost ([§C.6](../zadanie.md#c6-pozorovateľnosť)).

**Fix.** Restore PostgreSQL ([backup-restore.md](backup-restore.md)); nothing on the edge needs a restart. To widen the window the edge can ride out, raise `Dle:Edge:Cache:L2Minutes` and run Valkey — with L1 only, a restart of the edge empties the cache.

## Still stuck

- Read the problem document: the `type` URI names the cause ([api.md — Errors](../integration/api.md#errors--rfc-9457-problem-details)).
- Turn on a trace for one request: set `Dle:Telemetry:TraceSampleRatio=1.0` on one edge replica briefly; the `resolve` span has `lookup`, `classify`, `route`, `render` children.
- The device matrix ([operations/device-test-matrix.md](../operations/device-test-matrix.md)) describes what "works" means per platform; several "bugs" are the platform behaving as documented.
