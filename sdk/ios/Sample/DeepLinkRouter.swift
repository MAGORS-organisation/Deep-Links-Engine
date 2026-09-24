import Foundation
import DleSDK

/// Turns a URL or a deferred link path into a screen — and nothing that is not on the
/// allowlist below becomes anything.
///
/// A link path comes from outside the app: from a URL anyone can craft, or from a link a
/// campaign manager typed into the console. It is validated against a fixed set of shapes and
/// otherwise dropped; it is never interpolated into a query, a file path or a web view URL.
@MainActor
final class DeepLinkRouter: ObservableObject {
    /// Where the app navigated to.
    enum Destination: Equatable {
        case home
        case promo(slug: String)
        case product(id: String)
    }

    /// The current screen.
    @Published private(set) var destination: Destination = .home

    /// A probabilistic hint from the engine, shown as a suggestion and never auto-navigated.
    @Published private(set) var suggestedPromo: String?

    /// The outcome of the deferred resolve, for the demo screen.
    @Published private(set) var resolveSummary = "not resolved yet"

    /// Slugs and identifiers: letters, digits, `-` and `_`, 1 to 64 characters.
    private static let token = try! NSRegularExpression(pattern: "^[A-Za-z0-9_-]{1,64}$")

    /// Routes a URL the operating system delivered. Only the path is consulted.
    func route(_ url: URL) {
        route(path: url.path)
    }

    /// Routes a path such as `/promo/autumn` or `/p/42`. Anything else goes home.
    func route(path: String) {
        let parts = path.split(separator: "/", omittingEmptySubsequences: true).map(String.init)
        switch parts.count {
        case 2 where parts[0] == "promo" && Self.isToken(parts[1]):
            destination = .promo(slug: parts[1])
        case 2 where parts[0] == "p" && Self.isToken(parts[1]):
            destination = .product(id: parts[1])
        default:
            destination = .home
        }
    }

    /// The once-per-installation resolve. Safe to call on every launch: after the first one the
    /// SDK answers from its persisted state without a request.
    func resolveDeferredLinkIfNeeded() async {
        guard let dle = try? Dle.shared else { return }
        do {
            let link = try await dle.resolve()
            apply(link)
        } catch {
            // No connectivity on first launch, or the engine refused. The app continues with
            // its normal onboarding; nothing about this is user-facing.
            resolveSummary = "resolve failed: \(error.localizedDescription)"
        }
    }

    /// Submits the code the user typed from the interstitial page.
    func submit(claimCode: String) async {
        guard let dle = try? Dle.shared else { return }
        do {
            apply(try await dle.submitClaimCode(claimCode))
        } catch DleError.claimCodeMalformed {
            resolveSummary = "that is not a six-character code"
        } catch DleError.claimCodeRejected(let reason, let canReissue) {
            resolveSummary = "code refused (\(reason.rawValue))" + (canReissue ? " — open the link again for a new one" : "")
        } catch {
            resolveSummary = "claim failed: \(error.localizedDescription)"
        }
    }

    private func apply(_ link: DeferredLink) {
        resolveSummary = "matched=\(link.matched) type=\(link.matchType.rawValue) confidence=\(link.confidence)"
        guard link.matched, let path = link.deeplinkPath else { return }
        if link.isDeterministic {
            route(path: path)
        } else if link.isProbabilisticHint, let slug = Self.promoSlug(in: path) {
            // A statistical match is a suggestion, never a certainty (spec §A.2.5).
            suggestedPromo = slug
        }
    }

    private static func promoSlug(in path: String) -> String? {
        let parts = path.split(separator: "/", omittingEmptySubsequences: true).map(String.init)
        guard parts.count == 2, parts[0] == "promo", isToken(parts[1]) else { return nil }
        return parts[1]
    }

    private static func isToken(_ value: String) -> Bool {
        let range = NSRange(value.startIndex..., in: value)
        return token.firstMatch(in: value, range: range) != nil
    }
}
