import Foundation

/// The answer of `POST /v1/resolve`. Mirrors `ResolveResponseDto` member for member.
///
/// ``matchType`` and ``confidence`` are not optional, on purpose: a body without either is
/// refused as malformed rather than guessed at, because a guess is exactly how a probabilistic
/// hint ends up displayed as a certainty (FR-186, ADR-008, spec §A.2.5). Read
/// ``isDeterministic`` before doing anything irreversible for the user.
public struct DeferredLink: Sendable, Hashable, Codable {
    /// The link an installation was attributed to, reduced to what the app needs to navigate.
    /// Mirrors `ResolveLinkDto`.
    public struct Link: Sendable, Hashable, Codable {
        /// Link identifier, as a string because the value is a 64-bit Snowflake.
        public let id: String
        /// Path the application should open, for example `/promo/autumn`.
        public let deeplinkPath: String?
        /// Campaign name carried by the link.
        public let campaign: String?
        /// Human-readable link title.
        public let title: String?

        /// Creates a link.
        public init(id: String, deeplinkPath: String? = nil, campaign: String? = nil, title: String? = nil) {
            self.id = id
            self.deeplinkPath = deeplinkPath
            self.campaign = campaign
            self.title = title
        }

        private enum CodingKeys: String, CodingKey {
            case id
            case deeplinkPath = "deeplink_path"
            case campaign
            case title
        }

        /// Decodes `ResolveLinkDto`; `id` is required.
        public init(from decoder: Decoder) throws {
            let c = try decoder.container(keyedBy: CodingKeys.self)
            id = try c.decode(String.self, forKey: .id)
            deeplinkPath = try c.decodeIfPresent(String.self, forKey: .deeplinkPath)
            campaign = try c.decodeIfPresent(String.self, forKey: .campaign)
            title = try c.decodeIfPresent(String.self, forKey: .title)
        }

        /// Encodes `ResolveLinkDto`, omitting absent members.
        public func encode(to encoder: Encoder) throws {
            var c = encoder.container(keyedBy: CodingKeys.self)
            try c.encode(id, forKey: .id)
            try c.encodeIfPresent(deeplinkPath, forKey: .deeplinkPath)
            try c.encodeIfPresent(campaign, forKey: .campaign)
            try c.encodeIfPresent(title, forKey: .title)
        }
    }

    /// Whether any strategy matched. When `false` the app runs its normal onboarding.
    public let matched: Bool
    /// The strategy that produced the match. Never optional.
    public let matchType: DleMatchType
    /// Confidence in `0.0 ... 1.0`. Exactly `1.0` for deterministic strategies; a probabilistic
    /// match is always below it. Never optional.
    public let confidence: Double
    /// Identifier of the matched click.
    public let clickId: String?
    /// The matched link, when there is one.
    public let link: Link?
    /// Parameters handed to the application, typically the link's UTM set plus custom data.
    /// Keys keep their original spelling.
    public let params: [String: String]
    /// Seconds this result stays valid. `0` means it is final and must not be re-requested.
    public let expiresIn: Int

    /// Creates a result by hand (tests, previews).
    public init(
        matched: Bool,
        matchType: DleMatchType,
        confidence: Double,
        clickId: String? = nil,
        link: Link? = nil,
        params: [String: String] = [:],
        expiresIn: Int = 0
    ) {
        self.matched = matched
        self.matchType = matchType
        self.confidence = min(1, max(0, confidence.isFinite ? confidence : 0))
        self.clickId = clickId
        self.link = link
        self.params = params
        self.expiresIn = max(0, expiresIn)
    }

    /// The result of an installation nobody could match: run the normal onboarding.
    public static let unmatched = DeferredLink(matched: false, matchType: .none, confidence: 0)

    /// `true` only for a match the engine established exactly, with full confidence:
    /// `login`, `claim_code`, `direct_open` (and `install_referrer` on Android).
    ///
    /// Only such a result may drive anything irreversible, such as granting a referral bonus.
    /// A `probabilistic` result is a hint even if a server bug reported `1.0`.
    public var isDeterministic: Bool {
        matched && matchType.isDeterministic && confidence >= 1
    }

    /// `true` for a statistical match. Present it to the user as a suggestion, never as fact.
    public var isProbabilisticHint: Bool {
        matched && matchType == .probabilistic
    }

    /// Shortcut for `link?.deeplinkPath`.
    public var deeplinkPath: String? {
        link?.deeplinkPath
    }

    /// `true` when the engine said this result will not change (`expires_in == 0`).
    public var isFinal: Bool {
        expiresIn == 0
    }

    private enum CodingKeys: String, CodingKey {
        case matched
        case matchType = "match_type"
        case confidence
        case clickId = "click_id"
        case link
        case params
        case expiresIn = "expires_in"
    }

    /// Decodes `ResolveResponseDto`.
    ///
    /// `match_type` and `confidence` must be present (the decoder throws otherwise). A missing
    /// `params` is an empty map, a missing `expires_in` is `0`, and a `link` without an `id` is
    /// treated as absent. `confidence` is clamped into `0 ... 1`.
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        matched = try c.decodeIfPresent(Bool.self, forKey: .matched) ?? false
        matchType = try c.decode(DleMatchType.self, forKey: .matchType)
        let raw = try c.decode(Double.self, forKey: .confidence)
        guard raw.isFinite else {
            throw DecodingError.dataCorruptedError(
                forKey: .confidence, in: c, debugDescription: "confidence is not a finite number")
        }
        confidence = min(1, max(0, raw))
        clickId = try c.decodeIfPresent(String.self, forKey: .clickId)
        link = (try? c.decodeIfPresent(Link.self, forKey: .link)) ?? nil
        params = try c.decodeIfPresent([String: String].self, forKey: .params) ?? [:]
        expiresIn = max(0, try c.decodeIfPresent(Int.self, forKey: .expiresIn) ?? 0)
    }

    /// Encodes `ResolveResponseDto` (used to persist the once-per-installation result).
    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(matched, forKey: .matched)
        try c.encode(matchType, forKey: .matchType)
        try c.encode(confidence, forKey: .confidence)
        try c.encodeIfPresent(clickId, forKey: .clickId)
        try c.encodeIfPresent(link, forKey: .link)
        try c.encode(params, forKey: .params)
        try c.encode(expiresIn, forKey: .expiresIn)
    }
}
