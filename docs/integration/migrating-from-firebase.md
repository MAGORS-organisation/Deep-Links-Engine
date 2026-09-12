# Migrating from Firebase Dynamic Links

**What this is:** a concrete mapping from Firebase Dynamic Links concepts to this engine, and a cutover
order. **Who it is for:** a team whose links stopped working on 25 August 2025 and who chose not to buy
a commercial measurement partner.

## What actually changed on 25 August 2025

Firebase Dynamic Links was shut down completely: `*.page.link` domains answer 404 and the API answers
400 or 403. There is nothing to redirect *from*; every link you had is already dead. The migration is
therefore about your **own** domain, your **own** association files, and your **app**, not about
forwarding old URLs.

## Concept mapping

| Firebase Dynamic Links | Deep Link Engine | Notes |
|---|---|---|
| `https://example.page.link/abc` | `https://link.example.com/<slug>` on a domain you register | You own the domain and serve its association files now ([self-hosting/domains.md](../self-hosting/domains.md)) |
| `link=` (deep link URL) | `target_url` + `deeplink_path` | The web target is the fallback; the path is what the app receives |
| `apn=` (Android package) / `ibi=` (iOS bundle) | Registered apps, associated with the domain | Plus the Play App Signing fingerprint and Apple team id |
| `afl=` / `ifl=` (per-platform fallback) | Routing rules with `platform: ["android"]` / `["ios"]` and `action: web` | [architecture/routing-rules.md](../architecture/routing-rules.md) |
| `ofl=` (desktop fallback) | The mandatory `default` rule | Every rule set must end with one |
| `isi=` (App Store id), `apn` store page | `store_url` on the app / in the rule | The engine builds the store URL and, on Android, the `referrer` parameter |
| `st=`, `sd=`, `si=` (social meta) | `og.title`, `og.description`, `og.image_url` | Crawlers get a 200 HTML preview, never a redirect |
| `utm_*` | `utm` on the link | Merged into the web target; forwardable click ids (`gclid`, `fbclid`, …) pass through an allowlist |
| `efr=1` (skip preview page) | Interstitial mode `never` in the rule | Keep the interstitial for in-app webviews; it is what makes Universal Links fire there |
| `d=1` (debug) | `GET /api/v1/links/{id}/simulate` and `?_dl=preview` | Evaluates rules without writing a click |
| Deferred deep link on Android | Play Install Referrer, `match_type: install_referrer` | Same mechanism Firebase used; now deterministic and visible |
| Deferred deep link on iOS | Claim code, login reconciliation, direct open; probabilistic opt-in | Firebase relied on the pasteboard, which iOS 16 made unusable ([sdk-ios.md](sdk-ios.md)) |
| Firebase Analytics events | `POST /v1/events` and your own analytics | The engine reports attribution; it is not an analytics suite |

## Cutover order

1. **Domain first.** Register `link.example.com`, point DNS at the engine, and get the domain page to
   show AASA and assetlinks verified. Apple's CDN needs up to 24 hours and devices refresh weekly;
   Android 15+ can take up to seven days. Start this before anything else.
2. **Apps.** Register the iOS app (bundle id, team id) and the Android app (package, **Play App Signing**
   SHA-256, not the upload key).
3. **Links.** Export your Firebase link inventory (the console's CSV export, if you kept one; the API is
   gone). Convert each row with the mapping above and import with `POST /api/v1/links/bulk` as NDJSON —
   ten thousand rows per batch ([api.md](api.md)). Custom slugs are allowed if you want to keep the old
   short codes; they must be 3–7 or 9+ characters so they cannot collide with generated ones.
4. **App release.** Replace the Firebase SDK calls with the engine SDK, add the associated-domains
   entitlement and the App Links intent filter, and ship. Until the new app version is widely installed,
   direct opens still work through the OS — but only the new version reports them.
5. **Consent.** Decide the tenant's consent mode. `aggregate_only` needs no banner and gives campaign-level
   numbers; `full` needs recorded consent and enables per-user attribution
   ([compliance/privacy.md](../compliance/privacy.md)). Firebase never asked you this question; the EDPB
   Guidelines 2/2023 do.
6. **Verify on real devices.** The eight combinations in
   [operations/device-test-matrix.md](../operations/device-test-matrix.md). Simulators do not reproduce
   Universal Link behaviour.

## What you gain and what you give up

You gain: your data on your infrastructure, a controller role with no processor agreement, honest
confidence on every attribution, and links whose targets you can change without a 301 ever being cached.

You give up: Firebase's hosted domain and its analytics dashboard, and any illusion that iOS deferred
deep linking is precise. The specification's own words: anyone promising accurate iOS deferred deep
links in 2026 is not telling the truth ([§A.2.4](../zadanie.md)).
