import Foundation

/// The user's consent state, as the host app collected it.
///
/// The engine is consent-first: the consent signal is an **input to the decision pipeline**,
/// not a post-hoc filter (spec §E.6.2, SHARED-KERNEL §2). Concretely, for this SDK:
///
/// * `attribution == false` ⇒ the SDK omits the `signals` object from `POST /v1/resolve`
///   entirely, so there is nothing for the server to discard (TC-145 / TC-146).
/// * `analytics == false` ⇒ behavioural events (`session`, `conversion`, `custom`) are not
///   queued at all, unless the integrator opts out of that gate in ``DleConfig``.
///
/// The SDK never infers consent. If you do not call ``Dle/updateConsent(_:)``, the value from
/// ``DleConfig/consent`` is used, and its default is *denied*.
public struct DleConsent: Sendable, Hashable, Codable {
    /// The user agreed to measurement of in-app behaviour.
    public var analytics: Bool
    /// The user agreed to linking this install to a prior click.
    public var attribution: Bool
    /// When the decision was recorded. Sent to the server as `consent.ts`.
    public var timestamp: Date

    /// Creates a consent record.
    /// - Parameters:
    ///   - analytics: whether behavioural measurement is allowed.
    ///   - attribution: whether click-to-install linking is allowed.
    ///   - timestamp: when the user made the decision. Defaults to now.
    public init(analytics: Bool, attribution: Bool, timestamp: Date = Date()) {
        self.analytics = analytics
        self.attribution = attribution
        self.timestamp = timestamp
    }

    /// Nothing is allowed. This is the SDK's default.
    public static func denied(at timestamp: Date = Date()) -> DleConsent {
        DleConsent(analytics: false, attribution: false, timestamp: timestamp)
    }

    /// Everything is allowed.
    public static func granted(at timestamp: Date = Date()) -> DleConsent {
        DleConsent(analytics: true, attribution: true, timestamp: timestamp)
    }

    /// Behavioural measurement only; no click-to-install linking and no device signals.
    public static func analyticsOnly(at timestamp: Date = Date()) -> DleConsent {
        DleConsent(analytics: true, attribution: false, timestamp: timestamp)
    }

    /// Whether device signals may be collected and transmitted at all.
    ///
    /// Signals are additionally gated on the probabilistic strategy being switched on in
    /// ``DleConfig/deferredStrategies`` — consent alone does not turn fingerprint-adjacent
    /// collection on.
    public var allowsDeviceSignals: Bool { attribution }

    private enum CodingKeys: String, CodingKey {
        case analytics
        case attribution
        case timestamp = "ts"
    }
}
