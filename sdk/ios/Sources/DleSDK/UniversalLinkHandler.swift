import Foundation

#if canImport(UIKit) && !os(watchOS)
import UIKit
#endif

// MARK: - Link policy

/// Decides which incoming URLs are the engine's links: `http(s)` on one of the hosts the app
/// lists in its `applinks:` entitlement (``DleConfig/linkHosts``).
///
/// The policy exists so the SDK never sends a URL of an arbitrary host to the engine. A URL that
/// the operating system hands the app can come from anywhere — a `mailto:`, a custom scheme, a
/// universal link of some other domain — and only the operator's own short links are the
/// engine's business (FR-223, spec §E.7 item 1).
public enum DleLinkPolicy {
    /// Normalises one ``DleConfig/linkHosts`` entry: lower-cased, trimmed, without a trailing
    /// dot; an optional leading `*.` matches exactly one subdomain level.
    ///
    /// - Returns: The pattern, or `nil` when the entry is not a bare host name (has a scheme,
    ///   a path, a port, a space, or is empty).
    public static func normalizeHostPattern(_ raw: String) -> String? {
        var host = raw.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        while host.hasSuffix(".") { host.removeLast() }
        guard !host.isEmpty else { return nil }
        let wildcard = host.hasPrefix("*.")
        let bare = wildcard ? String(host.dropFirst(2)) : host
        guard !bare.isEmpty, !bare.hasPrefix("*") else { return nil }
        let allowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: ".-"))
        guard bare.unicodeScalars.allSatisfy({ allowed.contains($0) }),
              !bare.hasPrefix("."), !bare.hasPrefix("-"), !bare.contains("..")
        else {
            return nil
        }
        return host
    }

    /// Whether `host` is covered by one of the (already normalised) `patterns`.
    ///
    /// An exact pattern matches the same host only. A `*.` pattern matches one level of
    /// subdomain (`*.example.com` matches `go.example.com`, not `example.com` and not
    /// `a.b.example.com`).
    public static func matches(host: String, patterns: [String]) -> Bool {
        var candidate = host.lowercased()
        while candidate.hasSuffix(".") { candidate.removeLast() }
        guard !candidate.isEmpty else { return false }
        for pattern in patterns {
            if pattern.hasPrefix("*.") {
                let suffix = pattern.dropFirst(2)
                guard candidate.hasSuffix("." + suffix) else { continue }
                let label = candidate.dropLast(suffix.count + 1)
                if !label.isEmpty, !label.contains(".") { return true }
            } else if candidate == pattern {
                return true
            }
        }
        return false
    }

    /// `true` when `url` is an `http` or `https` URL on one of `hosts`, and therefore a link the
    /// SDK reports as `link_open`. Everything else — custom schemes included — is the host
    /// app's own business and is never reported.
    public static func isReportable(_ url: URL, hosts: [String]) -> Bool {
        guard DleUniversalLink.isWebURL(url), let host = url.host, !host.isEmpty else { return false }
        return matches(host: host, patterns: hosts)
    }
}

// MARK: - URL helpers

/// Helpers for the URLs the operating system hands an app through a Universal Link.
public enum DleUniversalLink {
    /// `NSUserActivityTypeBrowsingWeb`, the activity type of a Universal Link continuation.
    /// Spelled out so the comparison compiles on every platform of the package.
    public static let browsingWebActivityType = "NSUserActivityTypeBrowsingWeb"

    /// `true` for `http` and `https` URLs. Case-insensitive, as schemes are.
    public static func isWebURL(_ url: URL) -> Bool {
        guard let scheme = url.scheme?.lowercased() else { return false }
        return scheme == "https" || scheme == "http"
    }

    /// The form of a URL that is reported as `EventDto.url`: the URL without its fragment.
    /// A fragment never reaches a server and can carry client-side state, so it is not the
    /// engine's to see. Everything else — including the query, which carries the click id and
    /// the UTM set — is kept verbatim.
    public static func reportable(_ url: URL) -> String {
        let absolute = url.absoluteString
        guard let hash = absolute.firstIndex(of: "#") else { return absolute }
        return String(absolute[..<hash])
    }

    /// The web page URL of a Universal Link continuation, or `nil` when `activity` is not a
    /// `NSUserActivityTypeBrowsingWeb` activity or carries no URL.
    public static func webpageURL(of activity: NSUserActivity) -> URL? {
        guard activity.activityType == browsingWebActivityType else { return nil }
        return activity.webpageURL
    }
}

// MARK: - Handler

/// Turns the three ways iOS delivers a link into one `link_open` report and one URL to route.
///
/// * `application(_:continue:restorationHandler:)` and `scene(_:continue:)` deliver an
///   `NSUserActivity` — ``handle(_:)``.
/// * `scene(_:willConnectTo:options:)` delivers the activities and URL contexts of a cold
///   start — ``handle(userActivities:)`` and ``handle(urlContexts:)``.
/// * SwiftUI's `.onOpenURL` and `scene(_:openURLContexts:)` deliver a `URL` — ``handle(url:)``.
///
/// Why this matters: when the app is already installed, a tap on a Universal Link opens it
/// **directly** — no request reaches the engine — so the click is invisible unless the app
/// reports it. This is the `link_open` event of FR-223 and spec §B.6.4, and the most
/// successful campaigns are the ones most under-counted without it.
///
/// Every method returns the URL for the host app to route and never routes itself. Only
/// `http(s)` URLs on the configured ``DleConfig/linkHosts`` are reported; a custom-scheme URL
/// is returned unreported and is never logged in full, because a custom-scheme URL is exactly
/// where a careless integration would put a token (spec §E.7 item 1).
///
/// Synchronous and callable from any thread: UIKit's callbacks must return a `Bool` without
/// leaving the call stack, and ``EventQueue/enqueue(_:)`` is synchronous for that reason.
public struct UniversalLinkHandler: Sendable {
    /// Receives the `link_open` event of a reportable URL.
    public typealias Reporter = @Sendable (DleEvent) -> Void

    /// Normalised host patterns, as ``DleConfig/validated()`` produces them.
    public let linkHosts: [String]

    private let reporter: Reporter
    private let warnedEmptyHosts = Locked(false)

    /// Creates a handler.
    /// - Parameters:
    ///   - linkHosts: Host patterns of the app's short links, already normalised through
    ///     ``DleLinkPolicy/normalizeHostPattern(_:)``.
    ///   - reporter: Where a `link_open` event goes — ``Dle/track(_:)`` in production.
    public init(linkHosts: [String], reporter: @escaping Reporter) {
        self.linkHosts = linkHosts
        self.reporter = reporter
    }

    /// Handles a Universal Link continuation (`NSUserActivityTypeBrowsingWeb`).
    ///
    /// - Returns: The web page URL to route, or `nil` when the activity is not a Universal Link
    ///   continuation — in which case the caller should return `false` to UIKit.
    @discardableResult
    public func handle(_ activity: NSUserActivity) -> URL? {
        guard let url = DleUniversalLink.webpageURL(of: activity) else { return nil }
        report(url)
        return url
    }

    /// Handles a URL delivered by SwiftUI's `.onOpenURL`, `scene(_:openURLContexts:)` or
    /// `application(_:open:options:)`.
    ///
    /// - Returns: The URL to route. `nil` only for a URL without a scheme, which nothing can
    ///   route.
    @discardableResult
    public func handle(url: URL) -> URL? {
        guard url.scheme != nil else { return nil }
        report(url)
        return url
    }

    /// Handles the user activities of a cold start (`UIScene.ConnectionOptions.userActivities`).
    ///
    /// - Returns: The URL of the first Universal Link continuation among them, or `nil`.
    @discardableResult
    public func handle(userActivities: Set<NSUserActivity>) -> URL? {
        for activity in userActivities {
            if let url = handle(activity) { return url }
        }
        return nil
    }

    #if canImport(UIKit) && !os(watchOS)
    /// Handles the URL contexts of `scene(_:openURLContexts:)` or of a cold start
    /// (`UIScene.ConnectionOptions.urlContexts`). Main-actor bound because `UIOpenURLContext` is.
    ///
    /// - Returns: The URLs to route, in the set's order.
    @MainActor
    @discardableResult
    public func handle(urlContexts: Set<UIOpenURLContext>) -> [URL] {
        urlContexts.compactMap { handle(url: $0.url) }
    }
    #endif

    /// Reports `url` when the policy allows it, and logs nothing that could identify a user.
    private func report(_ url: URL) {
        guard DleUniversalLink.isWebURL(url) else {
            // A custom-scheme URL: not ours to report, and not logged in full — only its scheme.
            DleLog.debug("ignoring non-web URL (scheme \(url.scheme ?? "?"))")
            return
        }
        if linkHosts.isEmpty {
            let alreadyWarned = warnedEmptyHosts.withLock { warned -> Bool in
                defer { warned = true }
                return warned
            }
            if !alreadyWarned {
                DleLog.warning("DleConfig.linkHosts is empty; no link_open will be reported")
            }
            return
        }
        guard DleLinkPolicy.isReportable(url, hosts: linkHosts) else {
            DleLog.debug("URL host is not in linkHosts; not reported: \(DleLog.redact(url))")
            return
        }
        DleLog.info("link_open: \(DleLog.redact(url))")
        reporter(DleEvent.linkOpen(url: url))
    }
}
