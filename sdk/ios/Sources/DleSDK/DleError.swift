import Foundation

/// Why the engine refused a claim code (`reason` member of the `claim-code-invalid` problem,
/// TC-148). The raw values are the wire values.
public enum DleClaimCodeRejection: String, Sendable, Hashable, CaseIterable {
    /// The code passed its time to live. Ask the user to read a fresh one off the link page.
    case expired
    /// The code was already redeemed once. The attribution it stood for has been made.
    case consumed
    /// Not six characters of the claim-code alphabet. Only reachable if the local check in
    /// ``DleClaimCode/isWellFormed(_:)`` is bypassed.
    case malformed
    /// Claim codes are switched off on this deployment.
    case disabled
    /// No such code was issued for this tenant, or the engine gave no reason.
    case unknown
}

/// An RFC 9457 problem document as the engine sends it, reduced to the members an SDK can
/// branch on. Anything else in the body is ignored.
public struct DleProblem: Sendable, Hashable, Decodable {
    /// Base URI every problem `type` is derived from (`ProblemCodes.Base`).
    public static let base = "https://docs.dle.dev/problems/"
    /// The request body failed validation.
    public static let validationFailed = base + "validation-failed"
    /// A rate limit was exceeded (spec §E.9). Carries `Retry-After`.
    public static let rateLimited = base + "rate-limited"
    /// The SDK key is missing, malformed, unknown or inactive.
    public static let unauthorized = base + "unauthorized"
    /// The credential lacks the scope the operation needs.
    public static let forbidden = base + "forbidden"
    /// The claim code cannot be redeemed (TC-148). Carries `reason` and `can_reissue`.
    public static let claimCodeInvalid = base + "claim-code-invalid"
    /// A dependency the engine needs did not answer.
    public static let dependencyUnavailable = base + "dependency-unavailable"

    /// Stable problem type URI.
    public let type: String?
    /// Short human-readable summary.
    public let title: String?
    /// HTTP status, repeated in the body.
    public let status: Int?
    /// Explanation specific to this occurrence. Never shown to end users verbatim.
    public let detail: String?
    /// The request path.
    public let instance: String?
    /// Extension: machine-readable reason of a claim-code refusal.
    public let reason: String?
    /// Extension: whether obtaining a fresh claim code is a sensible next step.
    public let canReissue: Bool?

    private enum CodingKeys: String, CodingKey {
        case type, title, status, detail, instance, reason
        case canReissue = "can_reissue"
    }

    /// Creates a problem document by hand (tests, custom transports).
    public init(
        type: String? = nil,
        title: String? = nil,
        status: Int? = nil,
        detail: String? = nil,
        instance: String? = nil,
        reason: String? = nil,
        canReissue: Bool? = nil
    ) {
        self.type = type
        self.title = title
        self.status = status
        self.detail = detail
        self.instance = instance
        self.reason = reason
        self.canReissue = canReissue
    }

    /// Decodes leniently: every member is optional and a wrongly typed one is treated as absent.
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        type = (try? c.decodeIfPresent(String.self, forKey: .type)) ?? nil
        title = (try? c.decodeIfPresent(String.self, forKey: .title)) ?? nil
        status = (try? c.decodeIfPresent(Int.self, forKey: .status)) ?? nil
        detail = (try? c.decodeIfPresent(String.self, forKey: .detail)) ?? nil
        instance = (try? c.decodeIfPresent(String.self, forKey: .instance)) ?? nil
        reason = (try? c.decodeIfPresent(String.self, forKey: .reason)) ?? nil
        canReissue = (try? c.decodeIfPresent(Bool.self, forKey: .canReissue)) ?? nil
    }

    /// The claim-code rejection this problem describes, or `nil` when it is not one.
    public var claimCodeRejection: DleClaimCodeRejection? {
        guard type == Self.claimCodeInvalid else { return nil }
        return DleClaimCodeRejection(rawValue: reason ?? "") ?? .unknown
    }
}

/// Every failure the SDK surfaces.
///
/// ``isRetriable`` is the property the event queue acts on: a retriable failure keeps the
/// events and backs off, a non-retriable one drops the batch, because a body the engine refuses
/// today it will refuse tomorrow.
public enum DleError: Error, Sendable, Hashable {
    /// ``Dle/shared`` was used before ``Dle/configure(_:)``.
    case notConfigured
    /// ``DleConfig`` is unusable. The message names the field.
    case invalidConfiguration(String)
    /// The method needs a strategy that ``DleConfig/deferredStrategies`` does not contain.
    case strategyDisabled(DleDeferredStrategies)
    /// The typed text is not six characters of the claim-code alphabet after normalisation.
    /// Nothing was sent.
    case claimCodeMalformed
    /// The engine refused the claim code (HTTP 400, `claim-code-invalid`).
    /// `canReissue` says whether showing the user a fresh code would help.
    case claimCodeRejected(reason: DleClaimCodeRejection, canReissue: Bool)
    /// The installation identifier could not be persisted, so a resolve would create an
    /// installation the engine could never match again.
    case installIdUnavailable
    /// The request never reached the engine, or the connection dropped. The text is a
    /// `URLError` code, never a URL.
    case network(String)
    /// The request exceeded ``DleConfig/requestTimeout``.
    case timeout
    /// The engine answered with a non-success status. `problem` is the parsed body when there
    /// was one; `retryAfter` is the server's `Retry-After` in seconds.
    case http(status: Int, problem: DleProblem?, retryAfter: TimeInterval?)
    /// A 2xx body could not be decoded. In particular a resolve response without `match_type`
    /// or `confidence` is refused rather than guessed at (FR-186).
    case malformedResponse(String)
    /// An event batch outside `1 ... 100` events was requested.
    case invalidBatch(count: Int)

    /// `true` when retrying later can succeed: network failures, timeouts, 408, 429 and 5xx.
    public var isRetriable: Bool {
        switch self {
        case .network, .timeout:
            return true
        case let .http(status, _, _):
            return status == 429 || status == 408 || status >= 500
        case .notConfigured, .invalidConfiguration, .strategyDisabled, .claimCodeMalformed,
             .claimCodeRejected, .installIdUnavailable, .malformedResponse, .invalidBatch:
            return false
        }
    }

    /// The server's suggested wait, when it gave one.
    public var retryAfter: TimeInterval? {
        if case let .http(_, _, retryAfter) = self { return retryAfter }
        return nil
    }

    /// The problem document, when the failure carried one.
    public var problem: DleProblem? {
        if case let .http(_, problem, _) = self { return problem }
        return nil
    }
}

extension DleError: LocalizedError {
    /// A short English description. Contains no key, no URL and no user data.
    public var errorDescription: String? {
        switch self {
        case .notConfigured:
            return "DleSDK is not configured; call Dle.configure(_:) first."
        case let .invalidConfiguration(message):
            return "DleSDK configuration is invalid: \(message)."
        case let .strategyDisabled(strategies):
            return "The strategy \(strategies.names.joined(separator: ",")) is not enabled in DleConfig.deferredStrategies."
        case .claimCodeMalformed:
            return "The claim code must be \(DleClaimCode.length) characters of \(DleClaimCode.alphabet)."
        case let .claimCodeRejected(reason, canReissue):
            return "The engine refused the claim code (\(reason.rawValue)); can reissue: \(canReissue)."
        case .installIdUnavailable:
            return "The installation identifier could not be stored in the Keychain."
        case let .network(code):
            return "The request failed before reaching the engine (\(code))."
        case .timeout:
            return "The request timed out."
        case let .http(status, problem, _):
            return "The engine answered HTTP \(status)\(problem?.title.map { ": \($0)" } ?? "")."
        case let .malformedResponse(message):
            return "The engine's response could not be read: \(message)."
        case let .invalidBatch(count):
            return "An event batch carries 1 to \(DleAPI.maxEventsPerBatch) events, not \(count)."
        }
    }
}
