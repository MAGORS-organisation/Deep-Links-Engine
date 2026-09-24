# Web SDK integration

**What this is:** the browser-side companion — a smart app banner and web fallback that never
fingerprints. **Who it is for:** the web developer adding `@magors/dle-web` to a site. The package README
at [`sdk/web/README.md`](../../sdk/web/README.md) has install and API details.

> Two limits first. `@magors/dle-web` is not published on npm; build it from `sdk/web` (see the README).
> And `dle-control` has no CORS support, so `resolve()` and event delivery from a page on another origin
> fail their preflight; they work only when the SDK plane is served on the page's own origin. The banner
> itself makes no request. Both are listed under [Known gaps](../../README.md#known-gaps).

## What it is and is not

The web SDK does two things: it renders an accessible smart banner that opens the app or the store, and
it reports web-side events so a click on your site can be attributed when consent allows. The
`<script>` build is about **9 kB gzip** (the build fails above 10 kB), has zero runtime dependencies,
and collects no canvas, WebGL, audio, font or plugin signals — nothing that could serve as a
fingerprint ([FR-227](../zadanie.md)).

## Usage

There is no CDN copy: build `dist/dle.global.js` and serve it from your own site.

```html
<script src="/vendor/dle.global.js"
        data-endpoint="https://www.example.com"
        data-sdk-key="dlk_…"
        data-banner='{"appName":"Example","openUrl":"https://link.example.com/aB3xK9pQ"}'></script>
```

`data-banner="true"` shows the banner with `document.title` and `location.href`; a JSON object supplies
the banner options. The script exposes `window.Dle`, and `Dle.getInstance()` returns the configured
client. `data-endpoint` is the SDK plane as the browser reaches it — the page's own origin, because of
the CORS gap above.

or with a bundler:

```ts
import { createDle } from '@magors/dle-web';
const dle = createDle({ endpoint: 'https://www.example.com', sdkKey: 'dlk_…' });
dle.setConsent({ analytics: true, attribution: false });
dle.showSmartBanner({ appName: 'Example', openUrl: 'https://link.example.com/aB3xK9pQ' });
```

## The banner

- Rendered into a shadow root so the host page's CSS cannot break it.
- Its primary action is a **real anchor element**. Inside an in-app webview a Universal or App Link
  fires only on a genuine tap on an anchor; `window.location` from script is not a user gesture on iOS
  ([§A.2.6](../zadanie.md)). The banner's anchor is that tap.
- WCAG 2.2 AA: visible focus, `aria-label`, Escape dismisses, dismissal remembered, honours
  `prefers-reduced-motion` and `prefers-color-scheme`. English and Slovak strings ship in the bundle.

## Channel detection

The SDK classifies the browser into the same channel names the server uses —
`browser`, `crawler`, `in_app_fb`, `in_app_ig`, `in_app_tiktok`, `in_app_linkedin`, `in_app_snapchat`, `in_app_x`,
`in_app_whatsapp`, `in_app_telegram`, `in_app_pinterest`, `in_app_other` — so a routing rule written in
the console means the same thing on both sides ([architecture/routing-rules.md](../architecture/routing-rules.md)).

## Consent and identifiers

An installation id is kept in `localStorage` only while attribution consent is granted; with consent
absent the SDK keeps an in-memory id for the page lifetime and sends no `signals`. The SDK does not read
a TCF consent string; call `setConsent` from your consent tool. Events go out in batches of at most 100;
when the page is hidden or unloaded one batch is sent with `fetch` `keepalive`. `navigator.sendBeacon` is
not used, because a beacon cannot carry the `Authorization` header the endpoint requires.

## Related

[api.md](api.md) · [compliance/privacy.md](../compliance/privacy.md)
