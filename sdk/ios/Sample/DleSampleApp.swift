import SwiftUI
import DleSDK

/// The smallest complete integration: configure once, resolve once, route every open.
///
/// Add this file, `ContentView.swift` and `DeepLinkRouter.swift` to an iOS app target that
/// depends on the `DleSDK` package, and give the target the associated-domains entitlement
/// described in `README.md`.
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
