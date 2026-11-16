# DleSDK for iOS

The Swift SDK of the [Deep Link Engine](https://github.com/MAGORS-organisation/Deep-Links-Engine)
— a self-hosted, EU-first deep linking and attribution engine. Swift Package, Swift 6 strict
concurrency, iOS 15+, **zero dependencies**: networking is `URLSession`, storage is the
Keychain and plain files.

It does three things:

1. **Resolves once.** On the first launch after install it asks the engine which link led
   here (`POST /v1/resolve`), persists the answer, and never asks again for that installation.
2. **Reports direct opens.** When the app is already installed, a Universal Link opens it
   directly and no request ever reaches the engine — the click is invisible unless the app
   says so. `Dle.handle(url:)` / `Dle.handle(_:)` queue that `link_open` event.
3. **Batches events.** `Dle.track(_:)` queues; the queue persists across launches, sends at
   most 100 events per request, retries with back-off, honours `Retry-After`, and flushes
   when the app comes to the foreground.

## Install

The repository root carries a `Package.swift` that exposes `DleSDK`, so the package can be added by
URL. The repository has no version tags yet, so pin a branch or a revision:

```swift
// Package.swift
.package(url: "https://github.com/MAGORS-organisation/Deep-Links-Engine", branch: "master"),
// target
.product(name: "DleSDK", package: "Deep-Links-Engine"),
```

or in Xcode: File → Add Package Dependencies… → enter the repository URL, choose a branch, and add
the `DleSDK` product to your target. A local checkout works too: Add Local… → select `sdk/ios`. The package ships its own `PrivacyInfo.xcprivacy`; Xcode
aggregates it into your app's privacy report automatically.

## Configure

```swift
import DleSDK

try Dle.configure(DleConfig(
    endpoint: URL(string: "https://link.example.com")!,   // your dle-control host
    sdkKey: "dle_pk_…",                                    // publishable, per application
    linkHosts: ["link.example.com"],                       // = your applinks: entitlement
    consent: .denied()))                                   // until the user decides
```

`endpoint` must be `https` (plain `http` is accepted for `localhost` only). `linkHosts` is the
allowlist behind `link_open`: only an `http(s)` URL on one of these hosts is reported. Leave
it empty and nothing is reported.

## Resolve the deferred link

```swift
let dle = try Dle.shared
let delivered = dle.deferredLink?.matched == true   // routed on a previous launch
let link = try await dle.resolve()
if delivered {
    // the stored answer again; nothing new to route
} else if link.matched, link.isDeterministic, let path = link.deeplinkPath {
    router.route(path: path)                 // validate `path` against your own allowlist first
} else if link.isProbabilisticHint {
    showSuggestion(link)                     // a hint, never a certainty, never a reward
}
```

Call it on every launch; only the first one costs a request, and every later one returns the same
stored answer — which is why the snippet routes on a match only once. The answer says how the match
was made (`matchType`) and how much to trust it (`confidence`). **Branch on
`isDeterministic` before doing anything irreversible** — granting a referral bonus, skipping
onboarding — because a `probabilistic` result is a statistical guess even if it looks
confident (spec §A.2.5, ADR-008).

Two deterministic supplements can be sent after the first resolve:

```swift
try await Dle.shared.submitClaimCode(typed)               // S3: the code shown on the link page
try await Dle.shared.reconcileLogin(loginKey: hashedId)   // S2: after sign-in; opaque, already hashed
```

The engine shows a claim code on the link page only under the tenant's `full` consent mode, and redeems
it only when the resolve carries attribution consent: with `attribution == false` the answer is
`matched == false` and the code stays unspent, so ask for consent before asking for the code. A
login match needs your signed-in web page to call the web SDK's `recordLoginHint` for that click;
this SDK sends the account key on resolve but records no hint
([Known gaps](../../README.md#known-gaps)).

`DleClaimCode.normalize` and `DleClaimCode.isWellFormed` mirror the engine's rules exactly,
so a code can be validated locally before the request. A refusal surfaces as
`DleError.claimCodeRejected(reason:canReissue:)`.

## Report opens

SwiftUI:

```swift
.onOpenURL { url in
    guard let routed = (try? Dle.shared)?.handle(url: url) else { return }
    router.route(routed)                     // your allowlist decides what `routed` becomes
}
```

UIKit: `Dle.shared.handle(userActivity)` in `application(_:continue:restorationHandler:)` or
`scene(_:continue:)`, `handle(userActivities:)` and `handle(urlContexts:)` in
`scene(_:willConnectTo:options:)`. Every variant reports the `link_open` (if the URL is one of
yours) and returns the URL for you to route. Custom-scheme URLs are returned unreported and
never logged in full.

The URL is the short link as tapped (`https://link.example.com/aB3xK9pQ`), not the link's
`deeplink_path`. Ask the engine which screen it leads to — `POST /v1/resolve-link`, which records
nothing, so the open is still counted exactly once:

```swift
let link = try await Dle.shared.resolveLink(shortURL)   // match_type direct_open, confidence 1.0
if link.matched, let path = link.deeplinkPath { router.open(path) }
```

## Events and consent

```swift
try Dle.shared.track(.conversion(name: "purchase", value: 24.9, currency: "EUR"))
try Dle.shared.track(.custom(name: "onboarding_done", properties: ["step": "3"]))
try Dle.shared.updateConsent(DleConsent(analytics: true, attribution: true))
```

Consent is an input, not a filter after the fact:

| Consent | Effect |
| --- | --- |
| `attribution == false` | The `signals` object is **omitted** from `POST /v1/resolve` — not sent empty. The engine links nothing: the answer is `none` with a non-final `expires_in`, and a claim code or login key is not redeemed. |
| `analytics == false` | `first_open`, `session`, `conversion`, `custom` are dropped at `track` time; withdrawing it also discards queued behavioural events. |
| any | `link_open` is always reported: it is a first-party observation of a URL the OS handed the app. |

Device signals are additionally gated on the operator opting in: they are collected only when
`DleConfig.deferredStrategies` contains `.probabilistic` **and** the user granted attribution
consent. Neither alone turns collection on. The default strategy set is deterministic only.

## What iOS cannot do — and what the SDK does instead

Be honest with your stakeholders about deferred deep linking on iOS (spec §A.2.4):

* **No Install Referrer.** Android's Play Store hands the app the click id at install time.
  iOS has no such channel; `match_type = install_referrer` never occurs on iOS.
* **Clipboard matching is dead.** Since iOS 16 every programmatic pasteboard read shows a
  system prompt, and iOS 26 tightens it further. This SDK contains no `UIPasteboard` call
  at all.
* **Fingerprinting is degraded and disallowed.** Safari's Advanced Fingerprinting Protection
  (on by default in Private Browsing since iOS 17 and extended in iOS 26) coarsens the
  signals a web page can see, so a probabilistic match between the interstitial and the app
  is both unreliable and, without consent, unlawful (ePrivacy art. 5(3)). It is therefore
  opt-in, consent-gated, capped at a confidence below 1.0, and never the default.

Hence the defaults: **claim code** (the user types six characters from the link page),
**login reconciliation** (an opaque account key sent after sign-in) and **direct open** (the
app is installed; the Universal Link opens it and the SDK reports it). All three are
deterministic. Probabilistic matching is available, off by default. On the engine side claim codes
need the tenant's `full` consent mode and the user's attribution consent, and login matching needs
a hint from your signed-in web page (see [Resolve the deferred link](#resolve-the-deferred-link)).

## Universal Links checklist

* `applinks:link.example.com` in the Associated Domains capability, and the same host in
  `DleConfig.linkHosts`.
* The engine serves `/.well-known/apple-app-site-association` for that host once the app's
  team id and bundle id are registered in the console.
* **`applinks:link.example.com?mode=developer` bypasses Apple's CDN during development. It
  must be removed before App Store submission** — the build is rejected with it, and in
  production the entitlement would silently do nothing (spec §A.2.1).
* Test by tapping a link in Notes or Messages; a link typed into Safari's address bar opens
  Safari by design.

## Privacy statement

* No `UIPasteboard` — the clipboard is never read.
* No `ASIdentifierManager`, no `AdSupport`, no App Tracking Transparency — the advertising
  identifier is never touched and no ATT prompt is ever shown because of this SDK.
* No `identifierForVendor`, no hashing of device attributes. The only identifier is a random
  UUID generated by the SDK, kept in the Keychain (`AfterFirstUnlockThisDeviceOnly`, never
  synchronised, never backed up). It survives app updates and rotates on reinstall.
* Device signals (UI language, screen size, UTC offset, hardware model — four values shared
  by millions of devices) are sent only with the probabilistic strategy enabled **and**
  attribution consent granted.
* Logs never contain the SDK key, a full URL with its query string, or the raw install id.
* `PrivacyInfo.xcprivacy`: `NSPrivacyTracking = false`, no tracking domains, no
  required-reason APIs (state is stored in files, not `UserDefaults`; no file timestamps are
  read), collected data limited to the installation id, product interaction, and the coarse
  signals above.

## Errors

Every failure is a `DleError`. `isRetriable` is `true` for network failures, timeouts, 408,
429 and 5xx (the event queue keeps the batch and backs off, honouring `Retry-After`); a 400,
401, 403 or 413 drops the batch, because a body the engine refuses today it refuses tomorrow.
Problem details (RFC 9457) are parsed into `DleProblem`.

## Sample and tests

* `Sample/` — a SwiftUI app with an allowlisting router; see `Sample/README.md`.
* `Tests/DleSDKTests/` — `swift test` (macOS host) or run the `DleSDK` scheme in Xcode. The
  wire tests assert against the literal bodies of the engine's own contract tests.

## Platform notes

The package also declares macOS, Mac Catalyst, tvOS and visionOS. `platform` is reported as
`ios` on iOS and visionOS and `desktop` on macOS and Catalyst; scene-level APIs
(`handle(urlContexts:)`) exist where UIKit does.

MIT licence.
