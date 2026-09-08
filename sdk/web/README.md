# @magors/dle-web

Web SDK for the [Deep Link Engine](https://github.com/MAGORS-organisation/Deep-Links-Engine) — the
self-hosted, EU-first deep linking and attribution engine. MIT licensed.

It does four things on a website: shows an accessible **smart app banner**, keeps an **offline
event queue** for `POST /v1/events`, can ask the engine for the **click context** of this browser
via `POST /v1/resolve`, and enforces the **consent gates** in front of all of that.

**This SDK does no fingerprinting.** It never reads the clipboard, never reads `document.referrer`,
never enumerates fonts, plugins, canvas or audio, never calls a third party, and never stores an
identifier without attribution consent. The only identifier it ever creates is a random UUID v4 in
`localStorage`; clear storage and you are a new user. That is the intended behaviour.

- Zero runtime dependencies. No CDN references — ship the file yourself.
- ESM + CJS + a plain `<script>` (IIFE) build. ES2020. < 8 kB min+gzip for the IIFE.
- Wire format is the engine's snake_case contract (`Dle.Domain.Contracts.SdkContracts`), asserted
  byte-for-byte in the tests against `tests/Dle.ContractTests`.
- UI strings ship in English and Slovak.

## Install

```sh
npm install @magors/dle-web
```

```ts
import { createDle } from '@magors/dle-web';

const dle = createDle({
  endpoint: 'https://links.example.sk',   // your dle-control SDK plane
  sdkKey: 'dle_…',                        // publishable SDK key
  smartBanner: {
    appName: 'Example',
    openUrl: 'https://links.example.sk/aB3xK9pQ', // an http(s) DLE link — never a custom scheme
  },
});
```

Without a bundler, copy `dist/dle.global.js` next to your site and configure it from the tag:

```html
<script src="/vendor/dle.global.js"
        data-endpoint="https://links.example.sk"
        data-sdk-key="dle_…"
        data-banner='{"appName":"Example","openUrl":"https://links.example.sk/aB3xK9pQ"}'
        data-language="auto"></script>
```

`window.Dle` then exposes the whole API and `Dle.getInstance()` returns the configured client.
`data-banner="true"` uses `document.title` and `location.href`.

## Consent first

Everything is off until your consent tool says otherwise. Call `setConsent` from your CMP callback;
the decision is stored (two booleans and a timestamp, no identifier) and survives reloads.

```ts
dle.setConsent({ analytics: true, attribution: true });
```

| Consent | What it enables |
|---|---|
| `analytics` | `track()` queues and sends events. Without it events are dropped, never buffered. |
| `attribution` | A persistent install id, `dl_cid`/`utm_*` from the page URL as `referrer`, and (only if you also set `probabilisticSignals: true`) coarse device signals. |

With `attribution: false` the `/v1/resolve` body has **no `signals` member at all** — not an empty
object — and the install id lives in memory for the page view only. Revoking attribution consent
deletes the stored id immediately.

## Resolve

```ts
const result = await dle.resolve();          // cached per tab; honours expires_in
if (result.matched && isDeterministic(result)) {
  // install_referrer / login / claim_code / direct_open with confidence 1.0
} else if (result.matchType === 'probabilistic') {
  // a hint with result.confidence < 1 — show it as a suggestion, never as fact
}
```

Every result carries `matchType` **and** `confidence`. A body without both is refused as malformed
rather than guessed at. Options: `claimCode`, `loginKey`, `referrer`, `force`.

The engine limits resolve to 5 calls per hour per install id; the SDK caches the result in
`sessionStorage` so repeated calls in one tab cost nothing.

## Events

```ts
dle.track({ type: 'link_open', url: 'https://link.zak.sk/aB3xK9pQ' });
dle.track({ type: 'conversion', name: 'purchase', value: 24.9, currency: 'EUR' });
await dle.flush(); // optional; flushes happen automatically
```

Types: `link_open`, `first_open`, `session`, `conversion`, `custom`. Events are persisted in
`localStorage`, sent in batches of at most 100, retried with exponential back-off (honouring
`Retry-After`), pruned after 30 days (the engine's own tolerance), and flushed with `fetch`
`keepalive` when the page is hidden or unloaded.

Why not `navigator.sendBeacon`? The endpoint authenticates with `Authorization: Bearer <sdkKey>`
and a beacon cannot carry headers, so it would be refused with 401. `fetch` with `keepalive` is
the unload-safe transport that authenticates. One batch is sent on unload and removed optimistically
(no acknowledgement can be awaited past unload; duplicates would be worse than a rare loss) — the
rest goes out on the next page load.

## Smart banner

Rendered into a **shadow root**, so host CSS cannot break it and it cannot leak into the page.
WCAG 2.2 AA: a labelled `region`, a real `<a>` for the call-to-action and a real `<button>` to
dismiss, 44 px targets, AA contrast in light and dark, a visible focus ring, `Escape` dismisses,
dismissal remembered for `dismissForDays` (default 7), `prefers-reduced-motion` and
`prefers-color-scheme` respected, `lang` set to `en` or `sk`.

The call-to-action is a plain anchor on purpose. Inside an in-app browser (Facebook, Instagram,
TikTok, …) the operating system hands a Universal Link / App Link to the app **only on a genuine
user tap on an `<a>`**; a scripted `location.href` is not a user gesture on iOS. The banner's anchor
is that tap. The SDK never redirects and never opens a custom URI scheme.

```ts
dle.showSmartBanner({ appName: 'Example', openUrl: 'https://links.example.sk/x', position: 'top',
  tagline: { en: 'Deals inside', sk: 'Zľavy v aplikácii' }, theme: 'auto', showOn: ['ios', 'android'] });
dle.hideSmartBanner();
```

## Platform and channel

```ts
dle.getPlatform(); // { platform: 'ios', channel: 'in_app_ig', isInAppWebView: true, isCrawler: false, osVersion: '17.5' }
```

Channel names are exactly those of the engine (`browser`, `crawler`, `in_app_fb`, `in_app_ig`,
`in_app_tiktok`, `in_app_linkedin`, `in_app_snapchat`, `in_app_x`, `in_app_whatsapp`,
`in_app_telegram`, `in_app_pinterest`, `in_app_other`, `app`).

## Errors

Failures are `DleError` with `code` (`config` | `network` | `timeout` | `http` | `malformed`),
`status`, `retriable`, `retryAfterMs` and the RFC 9457 `problem` document. `PROBLEM_TYPES` lists the
stable `type` URIs.

## Observability

Each request carries a fresh W3C `traceparent` header so the SDK request and the server span it
caused belong to one trace. The SDK never logs the key, the request body or a problem detail.

## Server prerequisites

The SDK plane must be reachable from the browser: serve it on the site's origin or configure CORS
on `dle-control` for the `Authorization`, `Content-Type` and `traceparent` request headers.

## Development

```sh
npm install
npm run lint && npm run typecheck && npm run build && npm test && npm run size
```
