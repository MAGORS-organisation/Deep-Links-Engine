# ADR-0009 — HTTP response shape: by client class, never 301

**What this is:** what a click actually receives from the edge, per kind of client, and the one status code that is banned.
**Who it is for:** anyone touching the edge response pipeline, and anyone who has just been asked "why not a 301, it's faster".

## Status

Accepted

**Status note (2026-09-24).** The in-app-webview interstitial is applied only when the matched rule's action is `app_or_store` (`ResolveAppOrStore` in [`RoutingEngine.cs`](../../src/Dle.Domain/Routing/RoutingEngine.cs)). A `web` or `store_only` rule is answered with a `302` inside Instagram, Facebook and the other webviews too, and a link created without `routing_rules` gets a single `web` default rule, so it never shows an interstitial. Where the interstitial is shown, its "open in app" button is a custom-scheme URL — never a Universal Link / App Link, because `deeplink_path` must be relative — or is absent when the app has no custom scheme. See [Known gaps](../../README.md#known-gaps).

## Date

Decided: in the specification ([§B.4 ADR-009](../zadanie.md#adr-009--tvar-http-odpovede)) · Recorded: 2026-09-11

## Context

Three facts about clients decide the response shape ([§A.2.6](../zadanie.md#a26-in-app-prehliadače-a-crawlery)): social crawlers do not reliably follow 30x redirects and do not run JavaScript, so a redirect gives them an empty preview; an in-app webview (Facebook, Instagram, TikTok, LinkedIn…) hands a Universal Link / App Link to the OS only on a **genuine tap on an `<a>` element** — `window.location` is not a user gesture; and a `301` is cached by browsers and CDNs indefinitely.

## Decision

| Client class | Response | Why |
|---|---|---|
| Known crawler (UA + reverse DNS) | **200 HTML** with OG / Twitter Card tags, no redirect | bots do not follow 30x reliably and run no JS; otherwise the preview is empty |
| In-app webview (FB / IG / TikTok / LinkedIn …) | **200 interstitial HTML** with a real `<a>` button | a Universal Link fires in a webview only on a real tap |
| Ordinary mobile browser | **302** to the target, or the interstitial by configuration | fast |
| Desktop | **302** to the web fallback | — |
| **Never** | **301** | a permanent redirect is cached by browsers and CDNs and breaks A/B splits, expiries and target changes |

At most **one** redirect hop. Every additional hop adds latency and risks losing the click-ID parameters. Related, from [§A.2.1](../zadanie.md#a21-apple-universal-links): `/.well-known/apple-app-site-association` and `/.well-known/assetlinks.json` are **never redirected** — Apple's CDN and Android's verifier do not follow redirects, and a link that "works on my phone" silently fails for everyone else.

In code the classes are the `DecisionKind` enum ([`src/Dle.Domain/Routing/DecisionKind.cs`](../../src/Dle.Domain/Routing/DecisionKind.cs)): `Web` (302), `Store` (302 with campaign/referrer parameters), `AppDirect`, `Interstitial` (200), `Blocked`, `NotFound` (404), `Gone` (410 for a quarantined link), `Preview` (200 with OG tags, also for `?_dl=preview`). A rule's `interstitial: never` never suppresses the interstitial for an in-app webview — it only suppresses it where a redirect actually works ([`InterstitialMode`](../../src/Dle.Domain/Routing/InterstitialMode.cs)).

## Consequences

- Positive: previews render, in-app webviews open the app, and a link's target, A/B split and time window can change at any time because nothing downstream has cached a permanent answer.
- Positive: the same rule action (`app_or_store`) produces the right shape per client without the author thinking about webviews ([architecture/routing-rules.md](../architecture/routing-rules.md)).
- Negative: an interstitial is a page, not a hop — it needs branding, i18n (EN + SK, [NFR-15](../zadanie.md#a5-nefunkčné-požiadavky)) and WCAG 2.2 AA ([NFR-16](../zadanie.md#a5-nefunkčné-požiadavky)).
- Negative: `302` responses are not cached, so every click reaches the edge. That is the point, and it is why the cache tier exists.
- Verification: the 404 / 410 / 302 paths are verified through `WebApplicationFactory` in the security suite; the crawler and webview classification lives in the domain tier (≥ 90 % coverage). Behaviour on real devices is the pending 8-device manual matrix ([§D.2.1](../zadanie.md#d21-povinná-manuálna-matica-pred-release-8-kombinácií)).

## Alternatives considered

| Alternative | Why it lost |
|---|---|
| `301` everywhere ("it is cacheable") | Cacheable is the defect: the first response is the last one the browser and CDN ever ask for |
| JavaScript redirect for everyone | Crawlers do not execute it; webviews do not treat it as a gesture |
| Redirect chain (short link → tracking hop → target) | Each hop costs latency and can drop the click ID; one hop maximum |

## References

- [docs/zadanie.md §B.4 ADR-009](../zadanie.md#adr-009--tvar-http-odpovede)
- [docs/zadanie.md §A.2.1](../zadanie.md#a21-apple-universal-links), [§A.2.6](../zadanie.md#a26-in-app-prehliadače-a-crawlery)
- [docs/zadanie.md §B.6.1](../zadanie.md#b61-rozlíšenie-kliku) — the resolve flow that ends in one of these responses
- `src/Dle.Domain/Routing/DecisionKind.cs`, `src/Dle.Domain/Routing/InterstitialMode.cs`, `src/Dle.Domain/Routing/ChannelNames.cs`
