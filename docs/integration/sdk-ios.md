# iOS SDK integration

**What this is:** what the iOS SDK can and cannot do, and what your app must do around it. **Who it is
for:** the iOS developer integrating the `DleSDK` Swift package. The package README at
[`sdk/ios/README.md`](../../sdk/ios/README.md) has the setup and sample; this page is the honest protocol
view.

> The iOS SDK builds and passes its tests on the iOS Simulator in CI (`sdk-ios.yml`), and has not been
> run in an app on a real device yet. Treat the first release as a candidate. It cannot be added by URL yet: SwiftPM needs
> `Package.swift` at the repository root and a version tag, and the package lives in `sdk/ios` with no
> tags. Add `sdk/ios` of a checkout as a local package ([Known gaps](../../README.md#known-gaps)).

## What iOS does not offer

Read this before promising anything to marketing ([§A.2.4](../zadanie.md)):

| Mechanism | Status | Consequence |
|---|---|---|
| An Install Referrer equivalent | Does not exist | No deterministic click-to-install hand-off from the App Store |
| Clipboard matching | Dead since iOS 16 (system dialog on programmatic read) | The SDK never reads `UIPasteboard` |
| Fingerprinting | Degraded by Safari's Advanced Fingerprinting Protection (iOS 26) | Probabilistic matching is opt-in, consent-gated, windowed to 60 minutes, and reports its confidence |

So the design offers three **deterministic** paths on iOS and one honest probabilistic one:

| Strategy | `match_type` | Confidence | What the user does |
|---|---|---|---|
| Direct open through a Universal Link | `direct_open` | 1.0 | Nothing — app already installed |
| Claim code from the interstitial | `claim_code` | 1.0 | Types a 6-character code in the app |
| Login reconciliation | `login` | 1.0 | Signs in; the app sends a hashed account key |
| Probabilistic (opt-in) | `probabilistic` | 0.3–0.9 | Nothing — and consent is required |

What works today, and under which conditions:

- **Direct open** works in every consent mode: `handle(url:)` reports the open and
  `resolveLink(url)` returns the screen (see [Universal Links](#universal-links)).
- **Claim code** works when the tenant's consent mode is `full`: the store-bound interstitial then
  shows a code (and only then — under `aggregate_only` and `off` it could never be redeemed, so none is
  shown), and the app redeems it with the user's attribution consent.
- **Login reconciliation** never matches yet: the strategy looks for a hashed account key on the click,
  and nothing records one there ([Known gaps](../../README.md#known-gaps)).

## First launch

```swift
let link = try await Dle.shared.resolve(claimCode: userTypedCode)   // claimCode optional
```

`POST /v1/resolve` is sent once with `install_id` (a UUID kept in the Keychain), `platform: "ios"`, the
app and OS versions, the consent you recorded with `updateConsent`, and the claim code or login key
when you have one (see above for when each can match). The result is persisted; later calls return it without a request, and the server rate-limits
the endpoint to 5 per hour per install.

The claim code alphabet is `ACDEFGHJKLMNPQRTUVWXY34679` — confusable characters removed — and the SDK
normalises input exactly as the server does, so `k7qp-2m` and `K7QP2M` are the same code. A code shown
on the interstitial is valid for one hour and can be redeemed once (a second redemption, even a
concurrent one, answers `consumed`); an expired or spent code returns a typed error with `can_reissue`
so you can offer a fresh one
([api.md](api.md#errors--rfc-9457-problem-details)).

## Universal Links

- Add the associated-domains entitlement: `applinks:link.example.com`. Each subdomain needs its own
  entry and its own association file; nothing is inherited.
- Route every `NSUserActivity` / `onOpenURL` through `Dle.shared.handle(...)`. The SDK reports
  `link_open` and hands you the URL to route. Without the report the direct open is invisible to
  analytics ([FR-223](../zadanie.md)).
- That URL is the short link as tapped (`https://link.example.com/aB3xK9pQ`), not the link's
  `deeplink_path`. Ask the engine for the screen with `resolveLink`:

  ```swift
  .onOpenURL { url in
      guard let shortURL = Dle.shared.handle(url: url) else { return }   // reports link_open
      Task {
          let link = try await Dle.shared.resolveLink(shortURL)          // POST /v1/resolve-link
          if link.matched, let path = link.deeplinkPath { router.open(path) }
      }
  }
  ```

  The answer has `matchType == .directOpen`, confidence 1.0, and the `deeplink_path` the link's
  routing rules choose for iOS. `resolveLink` records nothing itself, so the open is counted once.
  A URL that is not on `linkHosts`, or not a link of your tenant, answers ``DeferredLink/unmatched``.
- **Remove `?mode=developer`** from the entitlement before App Store submission. It bypasses Apple's
  CDN cache during development and is rejected in review ([§A.2.1](../zadanie.md)).
- Apple fetches your `apple-app-site-association` through its CDN within about 24 hours and devices
  refresh roughly weekly. Plan link-routing changes a week ahead ([self-hosting/domains.md](../self-hosting/domains.md)).
- Inside Instagram, Facebook and TikTok webviews a Universal Link fires only on a real user tap. That is
  why the engine serves an interstitial page with an actual anchor element rather than a redirect, for
  `app_or_store` rules ([ADR-0009](../adr/0009-http-response-shape-never-301.md)). Today that anchor
  is a custom-scheme URL, never a Universal Link (a `deeplink_path` is always relative), and it is
  absent when the app has no custom scheme ([Known gaps](../../README.md#known-gaps)).

## Consent and privacy

`DleConsent` is not persisted: the SDK keeps it in memory, starts every launch from
`DleConfig.consent`, and sends it with every resolve. Pass the decision your consent tool stored when you
call `Dle.configure`, and `updateConsent` when it changes. With `attribution == false`, or with the
probabilistic strategy absent from `DleConfig.deferredStrategies`, the `signals` object is omitted from
the body entirely. The package ships a `PrivacyInfo.xcprivacy` declaring `NSPrivacyTracking = false`,
no tracking domains and no required-reason API categories; it links neither `AdSupport` nor
`AppTrackingTransparency`.

The `install_id` is not consent-gated: the SDK creates it when it is configured and sends it with every
resolve and every event batch whatever the consent, and the server stores install and event rows in
every consent mode ([Known gaps](../../README.md#known-gaps)).

## Events

`Dle.shared.track(DleEvent)` queues to disk and flushes on foreground in batches of at most 100 to
`POST /v1/events`. Validate any deep-link path with an allowlist before navigating; the sample shows one.

## Related

[api.md](api.md) · [sdk-android.md](sdk-android.md) · [compliance/privacy.md](../compliance/privacy.md)
