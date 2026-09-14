import SwiftUI
import DleSDK

/// A demo screen: shows where a link took the app, lets the user grant or withdraw consent,
/// and lets them type a claim code. Nothing here is required by the SDK — it is a visible
/// surface for the three calls that matter: `handle`, `resolve` and `updateConsent`.
struct ContentView: View {
    @EnvironmentObject private var router: DeepLinkRouter
    @State private var analyticsConsent = false
    @State private var attributionConsent = false
    @State private var claimCode = ""

    var body: some View {
        // NavigationView rather than NavigationStack so the sample runs on the package's
        // iOS 15 floor.
        NavigationView {
            List {
                Section("Destination") {
                    switch router.destination {
                    case .home:
                        Text("Home")
                    case let .promo(slug):
                        Text("Promo: \(slug)")
                    case let .product(id):
                        Text("Product: \(id)")
                    }
                    if let suggestion = router.suggestedPromo {
                        Text("Were you looking for the '\(suggestion)' promo?")
                            .font(.footnote)
                            .foregroundStyle(.secondary)
                    }
                }

                Section("Deferred link") {
                    Text(router.resolveSummary).font(.footnote)
                    TextField("Claim code from the link page", text: $claimCode)
                        .textInputAutocapitalization(.characters)
                        .autocorrectionDisabled()
                    Button("Submit code") {
                        let code = claimCode
                        Task { await router.submit(claimCode: code) }
                    }
                    .disabled(!DleClaimCode.isWellFormed(DleClaimCode.normalize(claimCode)))
                }

                Section("Consent") {
                    Toggle("Analytics", isOn: $analyticsConsent)
                    Toggle("Attribution", isOn: $attributionConsent)
                }
                .onChange(of: analyticsConsent) { _ in applyConsent() }
                .onChange(of: attributionConsent) { _ in applyConsent() }

                Section("Installation") {
                    row("Install id", (try? Dle.shared.installId) ?? "not configured")
                    row("Pending events", "\((try? Dle.shared.pendingEventCount) ?? 0)")
                    Button("Track custom event") {
                        (try? Dle.shared)?.track(.custom(name: "sample_tap"))
                    }
                }
            }
            .navigationTitle("DLE Sample")
        }
        .navigationViewStyle(.stack)
    }

    private func row(_ label: String, _ value: String) -> some View {
        HStack {
            Text(label)
            Spacer()
            Text(value).foregroundStyle(.secondary).font(.footnote.monospaced())
        }
    }

    private func applyConsent() {
        (try? Dle.shared)?.updateConsent(DleConsent(analytics: analyticsConsent, attribution: attributionConsent))
    }
}
