# DleSDK sample (SwiftUI)

Three source files that show the whole integration: configure once, resolve once, route every
open, and validate what you route. They are plain Swift files, not an Xcode project, so they
can be dropped into any app target.

## Add them to an Xcode project

1. **Add the package.** File → Add Package Dependencies… → enter the repository URL
   (`https://github.com/MAGORS-organisation/Deep-Links-Engine`), or for local development
   File → Add Package Dependencies… → Add Local… → select `sdk/ios`. Add the `DleSDK` product
   to your app target.
2. **Add the sources.** Drag `DleSampleApp.swift`, `ContentView.swift` and
   `DeepLinkRouter.swift` into the app target (or copy their contents into your own files —
   `DleSampleApp.swift` declares `@main`, so remove your own `@main` App or merge the two).
3. **Set the endpoint and key.** In `DleSampleApp.init`, replace `https://link.example.com`
   with your `dle-control` host and `dle_pk_replace_me` with the SDK key of your application
   (Console → Applications → SDK keys). The key is publishable; it identifies the tenant and
   the app, it is not a secret.
4. **Associated domains.** Target → Signing & Capabilities → `+ Capability` → *Associated
   Domains* → add `applinks:link.example.com` (your short-link host). Xcode writes this
   entitlement:

   ```xml
   <key>com.apple.developer.associated-domains</key>
   <array>
       <string>applinks:link.example.com</string>
   </array>
   ```

   The host must serve `https://link.example.com/.well-known/apple-app-site-association`
   with your team id and bundle id — the engine's edge does this once the app is registered
   in the console. Until Apple's CDN has fetched it, taps open Safari instead of the app.

   During development you can bypass the CDN with `applinks:link.example.com?mode=developer`
   (iOS 16+, only on devices with Developer Mode on). **Remove `?mode=developer` before
   submitting to the App Store** — a build that ships with it is rejected, and the flag makes
   the entitlement silently do nothing in production.

5. **Match `linkHosts` to the entitlement.** `DleConfig.linkHosts` must list the same hosts.
   The SDK only reports a `link_open` for `http(s)` URLs on those hosts; that is what keeps it
   from sending URLs of arbitrary origins to your engine.

6. **Custom scheme (optional).** If you also register a URL scheme (Info → URL Types), the same
   `.onOpenURL` receives those opens. The SDK returns them for routing and never reports them —
   never put a token, a claim code or anything personal on a custom-scheme URL.

## What the sample does

| Where | What |
| --- | --- |
| `DleSampleApp.init` | `Dle.configure(_:)` with endpoint, key, `linkHosts`, denied consent, deterministic strategies. |
| `.task` on the root view | `DeepLinkRouter.resolveDeferredLinkIfNeeded()` → `Dle.shared.resolve()`. The first call after install posts `POST /v1/resolve`; every later call returns the persisted answer without a request. Safe to run on every launch. |
| `.onOpenURL` | `Dle.shared.handle(url:)` reports `link_open` for a Universal Link (a direct open reaches no server, so this is the only record of the click) and returns the URL, which the router validates and routes. |
| `DeepLinkRouter.route` | The allowlist. Only `/promo/<slug>` and `/p/<id>` with `[A-Za-z0-9_-]{1,64}` become screens; anything else goes home. The path is never interpolated into a query, a file path or a web view. |
| `DeepLinkRouter.apply` | A deterministic result (`isDeterministic`) navigates. A probabilistic one (`isProbabilisticHint`) becomes a suggestion the user can dismiss — never an automatic navigation and never a reward. |
| `ContentView` consent toggles | `Dle.shared.updateConsent(_:)`. Withdrawing analytics consent also discards queued behavioural events. |
| Claim-code field | `DleClaimCode.normalize` + `isWellFormed` gate the button locally; `Dle.shared.submitClaimCode(_:)` sends it. `claimCodeRejected(reason:canReissue:)` tells the UI whether showing a fresh code would help. |

## UIKit instead of SwiftUI

```swift
// AppDelegate
func application(_ application: UIApplication,
                 continue userActivity: NSUserActivity,
                 restorationHandler: @escaping ([UIUserActivityRestoring]?) -> Void) -> Bool {
    guard let url = try? Dle.shared.handle(userActivity) else { return false }
    router.route(url)
    return true
}

// SceneDelegate
func scene(_ scene: UIScene, willConnectTo session: UISceneSession, options: UIScene.ConnectionOptions) {
    if let dle = try? Dle.shared {
        if let url = dle.handle(userActivities: options.userActivities) { router.route(url) }
        for url in dle.handle(urlContexts: options.urlContexts) { router.route(url) }
    }
}

func scene(_ scene: UIScene, continue userActivity: NSUserActivity) {
    if let url = try? Dle.shared.handle(userActivity) { router.route(url) }
}
```
