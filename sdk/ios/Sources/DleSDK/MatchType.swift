import Foundation

/// How an attribution was established.
///
/// Wire values match `MatchTypeNames` in the shared kernel (SHARED-KERNEL §8) and the
/// `match_type` field of `POST /v1/resolve` (spec §B.7.2).
///
/// The product rule that this enum exists to enforce: **a probabilistic match is never
/// presented as a certainty** (spec §A.2.5). Always read `confidence` alongside this value and
/// branch on ``isDeterministic`` before doing anything irreversible for the user.
public enum DleMatchType: String, Sendable, Hashable, CaseIterable, Codable {
    /// No attribution could be established. The app should run its normal onboarding.
    case none = "none"
    /// Android Play Install Referrer carried the click id. **Never produced on iOS** — the
    /// platform has no equivalent channel (spec §A.2.4).
    case installReferrer = "install_referrer"
    /// The user signed in and the account was reconciled with a pending click.
    case login = "login"
    /// The user typed the short code shown on the interstitial page (FR-184, spec §B.6.3).
    case claimCode = "claim_code"
    /// A statistical match inside the configured window. Opt-in, consent-gated, and always
    /// accompanied by a confidence below 1.0.
    case probabilistic = "probabilistic"
    /// The app was already installed and the OS opened it straight from a Universal Link
    /// (spec §B.6.4). Reported by the SDK, not inferred by the server.
    case directOpen = "direct_open"

    /// `true` for match types that identify the click exactly, with no statistical inference.
    ///
    /// Only deterministic matches may drive irreversible or user-visible personalisation such
    /// as granting a referral bonus.
    public var isDeterministic: Bool {
        switch self {
        case .installReferrer, .login, .claimCode, .directOpen:
            return true
        case .none, .probabilistic:
            return false
        }
    }

    /// The match types this SDK can actually produce on iOS.
    ///
    /// `installReferrer` is absent by design: iOS has no Install Referrer API.
    public static let reachableOnIOS: Set<DleMatchType> = [.none, .login, .claimCode, .probabilistic, .directOpen]

    /// Decodes leniently: an unrecognised value from a newer server is treated as ``none``
    /// rather than failing the whole response. Unknown means "not attributed", never
    /// "attributed somehow".
    public init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        let raw = try container.decode(String.self)
        if let known = DleMatchType(rawValue: raw) {
            self = known
        } else {
            DleLog.warning("unknown match_type '\(raw)' from server, treating as none")
            self = .none
        }
    }
}
