import SwiftUI
import DleSDK

/// The smallest complete integration: configure once, resolve once, route every open.
///
/// Add this file, `ContentView.swift` and `DeepLinkRouter.swift` to an iOS app target that
/// depends on the `DleSDK` package (Xcode: Add Package Dependencies, Add Local, `sdk/ios`).
/// This file declares `@main`, so remove or merge the target's own `App`. Then:
///
/// 1. Register the app in the engine's console (Apps) with its Apple Team ID, bundle identifier
///    and link host. The edge then serves `/.well-known/apple-app-site-association` on that host;
///    until Apple's CDN has fetched it, taps open Safari instead of the app.
/// 2. Signing & Capabilities, + Capability, Associated Domains: add `applinks:<your link host>`
///    (here `applinks:link.example.com`). `applinks:<host>?mode=developer` bypasses Apple's CDN
///    during development and must be removed before App Store submission.
/// 3. Below, replace the endpoint with your `dle-control` host, `dle_pk_replace_me` with the
///    app's publishable SDK key (console, Apps, SDK keys), and list exactly the `applinks:`
///    hosts in `linkHosts`.
@main
struct DleSampleApp: App {
    @StateObject private var router = DeepLinkRouter()

    init() {
        do {
            try Dle.configure(DleConfig(
                endpoint: URL(string: "https://link.example.com")!,
                sdkKey: "dle_pk_replace_me",
                // Exactly the hosts of the `applinks:` entitlement. Only http(s) URLs on these
                // hosts are reported as link_open; everything else is returned unreported.
                linkHosts: ["link.example.com"],
                // Consent starts denied. Replace with the outcome of your consent dialogue.
                consent: .denied(),
                // Deterministic strategies only. Probabilistic matching is opt-in:
                // deferredStrategies: [.claimCode, .login, .probabilistic]
                deferredStrategies: .deterministic,
                logLevel: .info))
        } catch {
            // A configuration error is a programming error; surface it during development.
            assertionFailure("DleSDK configuration failed: \(error)")
        }
    }

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environmentObject(router)
                // Universal Links and custom-scheme URLs both arrive here. The SDK reports the
                // link_open for its own hosts and hands the URL back for routing.
                .onOpenURL { url in
                    guard let dle = try? Dle.shared, let routed = dle.handle(url: url) else { return }
                    router.route(routed)
                }
                // First launch after install: ask the engine once which link led here.
                .task {
                    await router.resolveDeferredLinkIfNeeded()
                }
        }
    }
}
