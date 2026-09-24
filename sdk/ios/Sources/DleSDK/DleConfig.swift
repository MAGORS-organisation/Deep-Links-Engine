import Foundation

/// The deferred deep-link strategies this installation takes part in (spec §B.6.3, ADR-008).
///
/// Every strategy is executed by the engine; this set only decides what the SDK *offers* it.
/// `claimCode` and `login` are deterministic and cost nothing until the host app calls
/// ``Dle/submitClaimCode(_:)`` or ``Dle/reconcileLogin(loginKey:)``. `probabilistic` is the one
/// that matters for privacy: while it is absent — the default — the SDK never collects device
/// signals, whatever the consent state says (spec §0.2, §A.6 item 4, FR-227).
public struct DleDeferredStrategies: OptionSet, Sendable, Hashable {
    /// The underlying bit set.
    public let rawValue: UInt8

    /// Creates a strategy set from its raw bits.
    public init(rawValue: UInt8) {
        self.rawValue = rawValue
    }

    /// S3 — the user types the six-character code shown on the interstitial page (FR-184).
    /// Deterministic, confidence 1.00.
    public static let claimCode = DleDeferredStrategies(rawValue: 1 << 0)

    /// S2 — an opaque account key reported after sign-in is reconciled with the click that
    /// carried the same key (FR-185). Deterministic, confidence 1.00.
    public static let login = DleDeferredStrategies(rawValue: 1 << 1)

    /// S4 — coarse device signals are offered for a statistical match inside the engine's
    /// window (60 minutes by default). Opt-in, off by default, and additionally gated on
    /// ``DleConsent/attribution`` at call time. Never deterministic (spec §A.2.5).
    public static let probabilistic = DleDeferredStrategies(rawValue: 1 << 2)

    /// The default: deterministic strategies only.
    public static let deterministic: DleDeferredStrategies = [.claimCode, .login]

    /// Wire names of the contained strategies, for diagnostics.
    var names: [String] {
        var out: [String] = []
        if contains(.claimCode) { out.append(DleMatchType.claimCode.rawValue) }
        if contains(.login) { out.append(DleMatchType.login.rawValue) }
        if contains(.probabilistic) { out.append(DleMatchType.probabilistic.rawValue) }
        return out
    }
}

/// Static configuration of the SDK, passed once to ``Dle/configure(_:)``.
///
/// Everything here is a value; the SDK copies it and never reads it again, so mutating a
/// configuration after `configure` has no effect.
public struct DleConfig: Sendable {
    /// Base URL of the engine's SDK plane — the `dle-control` host, for example
    /// `https://links.example.sk`. `/v1/resolve` and `/v1/events` are appended to it.
    /// Must be `https`; plain `http` is accepted only for `localhost` during development.
    public var endpoint: URL

    /// The publishable SDK key, sent as `Authorization: Bearer <sdkKey>` (spec §B.7.2).
    ///
    /// It ships inside the application binary and is therefore an identifier, not a secret:
    /// it binds calls to one tenant and one application and can be revoked. The engine's
    /// per-installation rate limits are what protect the data.
    public var sdkKey: String

    /// Hosts of the short links this app handles, exactly as listed in the app's
    /// `applinks:` associated-domains entitlement (for example `["links.example.sk"]`).
    ///
    /// This is the allowlist behind ``Dle/handle(url:)``: only an `https` URL on one of these
    /// hosts is treated as a DLE link and reported as `link_open` (FR-223, spec §E.7 item 1).
    /// A pattern starting with `*.` matches one level of subdomain. With an empty list the SDK
    /// reports nothing and logs a warning, because it would otherwise have to send URLs of
    /// arbitrary hosts to the engine.
    public var linkHosts: [String]

    /// Consent to start with. Defaults to ``DleConsent/denied(at:)``; a later
    /// ``Dle/updateConsent(_:)`` replaces it.
    public var consent: DleConsent

    /// Which deferred strategies the SDK takes part in. Defaults to
    /// ``DleDeferredStrategies/deterministic``, which excludes probabilistic matching.
    public var deferredStrategies: DleDeferredStrategies

    /// Per-request timeout in seconds. Default 10.
    public var requestTimeout: TimeInterval

    /// Host application version reported as `app_version` (FR-124). Defaults to
    /// `CFBundleShortVersionString` of the main bundle.
    public var appVersion: String?

    /// Upper bound of the offline event queue. The oldest events are dropped beyond it.
    /// Default 200.
    public var maxQueuedEvents: Int

    /// Debounce, in seconds, between an event being queued and the automatic flush. Batching
    /// keeps the SDK inside the engine's budget of 60 events per minute per installation
    /// (spec §E.9). Default 3.
    public var flushDelay: TimeInterval

    /// Events older than this, in seconds, are discarded instead of sent: the engine refuses
    /// timestamps more than 30 days in the past (`RecordEvents.PastTolerance`). Default 30 days.
    public var maxEventAge: TimeInterval

    /// When `true` (the default), behavioural events — `first_open`, `session`, `conversion`,
    /// `custom` — are dropped at ``Dle/track(_:)`` time unless ``DleConsent/analytics`` is
    /// granted. `link_open` is never gated: it is a first-party observation of a URL the
    /// operating system handed to the app, and the engine records it without a consent check.
    public var gateEventsOnAnalyticsConsent: Bool

    /// Flush the queue whenever the application becomes active. Default `true`.
    public var flushOnForeground: Bool

    /// Namespace of the on-device state (Keychain account and storage directory), so two
    /// engines in one app do not share an installation identifier. Default `default`.
    public var storageNamespace: String

    /// Verbosity of ``DleLog``. Default ``DleLogLevel/warning``.
    public var logLevel: DleLogLevel

    /// Creates a configuration.
    /// - Parameters:
    ///   - endpoint: See ``endpoint``.
    ///   - sdkKey: See ``sdkKey``.
    ///   - linkHosts: See ``linkHosts``.
    ///   - consent: See ``consent``.
    ///   - deferredStrategies: See ``deferredStrategies``.
    ///   - requestTimeout: See ``requestTimeout``.
    ///   - appVersion: See ``appVersion``.
    ///   - maxQueuedEvents: See ``maxQueuedEvents``.
    ///   - flushDelay: See ``flushDelay``.
    ///   - maxEventAge: See ``maxEventAge``.
    ///   - gateEventsOnAnalyticsConsent: See ``gateEventsOnAnalyticsConsent``.
    ///   - flushOnForeground: See ``flushOnForeground``.
    ///   - storageNamespace: See ``storageNamespace``.
    ///   - logLevel: See ``logLevel``.
    public init(
        endpoint: URL,
        sdkKey: String,
        linkHosts: [String] = [],
        consent: DleConsent = .denied(),
        deferredStrategies: DleDeferredStrategies = .deterministic,
        requestTimeout: TimeInterval = 10,
        appVersion: String? = DleConfig.bundleVersion(),
        maxQueuedEvents: Int = 200,
        flushDelay: TimeInterval = 3,
        maxEventAge: TimeInterval = 30 * 24 * 60 * 60,
        gateEventsOnAnalyticsConsent: Bool = true,
        flushOnForeground: Bool = true,
        storageNamespace: String = "default",
        logLevel: DleLogLevel = .warning
    ) {
        self.endpoint = endpoint
        self.sdkKey = sdkKey
        self.linkHosts = linkHosts
        self.consent = consent
        self.deferredStrategies = deferredStrategies
        self.requestTimeout = requestTimeout
        self.appVersion = appVersion
        self.maxQueuedEvents = maxQueuedEvents
        self.flushDelay = flushDelay
        self.maxEventAge = maxEventAge
        self.gateEventsOnAnalyticsConsent = gateEventsOnAnalyticsConsent
        self.flushOnForeground = flushOnForeground
        self.storageNamespace = storageNamespace
        self.logLevel = logLevel
    }

    /// `CFBundleShortVersionString` of the main bundle, or `nil` outside an app bundle.
    public static func bundleVersion() -> String? {
        Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String
    }

    /// Returns a copy with hosts normalised and every field checked.
    /// - Throws: ``DleError/invalidConfiguration(_:)`` describing the first problem found.
    func validated() throws -> DleConfig {
        var copy = self

        copy.sdkKey = sdkKey.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !copy.sdkKey.isEmpty else {
            throw DleError.invalidConfiguration("sdkKey is empty")
        }

        guard let scheme = endpoint.scheme?.lowercased(), let host = endpoint.host, !host.isEmpty else {
            throw DleError.invalidConfiguration("endpoint must be an absolute https URL")
        }
        let loopback = host == "localhost" || host == "127.0.0.1" || host == "::1"
        guard scheme == "https" || (scheme == "http" && loopback) else {
            throw DleError.invalidConfiguration("endpoint must use https (http is allowed for localhost only)")
        }
        guard endpoint.query == nil, endpoint.fragment == nil else {
            throw DleError.invalidConfiguration("endpoint must not carry a query or fragment")
        }

        guard requestTimeout > 0 else { throw DleError.invalidConfiguration("requestTimeout must be positive") }
        guard maxQueuedEvents >= 1 else { throw DleError.invalidConfiguration("maxQueuedEvents must be at least 1") }
        guard flushDelay >= 0 else { throw DleError.invalidConfiguration("flushDelay must not be negative") }
        guard maxEventAge > 0 else { throw DleError.invalidConfiguration("maxEventAge must be positive") }

        let namespaceAllowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "._-"))
        guard !storageNamespace.isEmpty,
              storageNamespace.unicodeScalars.allSatisfy({ namespaceAllowed.contains($0) })
        else {
            throw DleError.invalidConfiguration("storageNamespace may contain letters, digits, '.', '_' and '-' only")
        }

        var hosts: [String] = []
        for raw in linkHosts {
            guard let normalized = DleLinkPolicy.normalizeHostPattern(raw) else {
                throw DleError.invalidConfiguration("linkHosts entry '\(raw)' is not a bare host name")
            }
            if !hosts.contains(normalized) { hosts.append(normalized) }
        }
        copy.linkHosts = hosts

        if let version = appVersion?.trimmingCharacters(in: .whitespacesAndNewlines) {
            copy.appVersion = version.isEmpty ? nil : String(version.prefix(64))
        }

        return copy
    }
}
