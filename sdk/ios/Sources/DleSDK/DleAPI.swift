import Foundation

// MARK: - Transport

/// Something that can send one HTTP request. `URLSession` in production, a stub in tests.
protocol DleTransport: Sendable {
    /// Sends the request and returns the body and response, or throws a transport error.
    func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse)
}

/// The production transport: an ephemeral `URLSession` with no cookies, no cache and no
/// redirect following. A bearer key must never be replayed to wherever a misconfigured proxy
/// points, so a 3xx is delivered as a response and refused, not followed.
///
/// `@unchecked Sendable`: `URLSession` is documented as thread-safe and the only stored state.
final class URLSessionTransport: DleTransport, @unchecked Sendable {
    private let session: URLSession

    /// Creates the session with the given per-request timeout.
    init(timeout: TimeInterval) {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = timeout
        configuration.timeoutIntervalForResource = timeout * 2
        configuration.httpCookieStorage = nil
        configuration.httpShouldSetCookies = false
        configuration.urlCache = nil
        configuration.requestCachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        configuration.waitsForConnectivity = false
        session = URLSession(configuration: configuration, delegate: RedirectRefuser(), delegateQueue: nil)
    }

    func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else {
            throw DleError.malformedResponse("not an HTTP response")
        }
        return (data, http)
    }
}

/// Refuses every redirect (see ``URLSessionTransport``).
private final class RedirectRefuser: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    func urlSession(
        _ session: URLSession,
        task: URLSessionTask,
        willPerformHTTPRedirection response: HTTPURLResponse,
        newRequest request: URLRequest
    ) async -> URLRequest? {
        nil
    }
}

// MARK: - Wire bodies (SdkContracts.cs, member for member, in contract order)

/// `DeviceSignalsDto`. Coarse by design; never a stable identifier.
struct DeviceSignalsBody: Encodable, Sendable, Hashable {
    /// BCP 47 language tag, for example `sk-SK`.
    var language: String?
    /// Physical pixels as `WIDTHxHEIGHT`.
    var screen: String?
    /// UTC offset in minutes (UTC+2 is `120`).
    var tzOffset: Int?
    /// Coarse hardware model.
    var deviceModel: String?

    private enum CodingKeys: String, CodingKey {
        case language
        case screen
        case tzOffset = "tz_offset"
        case deviceModel = "device_model"
    }

    func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encodeIfPresent(language, forKey: .language)
        try c.encodeIfPresent(screen, forKey: .screen)
        try c.encodeIfPresent(tzOffset, forKey: .tzOffset)
        try c.encodeIfPresent(deviceModel, forKey: .deviceModel)
    }
}

/// `ConsentDto`.
struct ConsentBody: Encodable, Sendable, Hashable {
    /// Consent to analytics processing.
    var analytics: Bool
    /// Consent to attribution processing.
    var attribution: Bool
    /// When the decision was recorded (`ts`).
    var timestamp: Date?

    /// Creates the body from the public consent value.
    init(_ consent: DleConsent) {
        analytics = consent.analytics
        attribution = consent.attribution
        timestamp = consent.timestamp
    }

    /// Creates the body field by field.
    init(analytics: Bool, attribution: Bool, timestamp: Date?) {
        self.analytics = analytics
        self.attribution = attribution
        self.timestamp = timestamp
    }

    private enum CodingKeys: String, CodingKey {
        case analytics, attribution, ts
    }

    func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(analytics, forKey: .analytics)
        try c.encode(attribution, forKey: .attribution)
        if let timestamp {
            try c.encode(DleWireDate.string(from: timestamp), forKey: .ts)
        }
    }
}

/// `ResolveRequestDto`. Absent optional members are omitted, never written as `null`.
struct ResolveRequestBody: Encodable, Sendable, Hashable {
    /// `install_id`.
    var installId: String
    /// `platform`: `ios`, `android`, `desktop` or `other`.
    var platform: String
    /// `app_version`.
    var appVersion: String?
    /// `os_version`.
    var osVersion: String?
    /// `referrer`. The Android Install Referrer; never set by this SDK, present so the model
    /// mirrors the contract and can reproduce the contract test's Android literal.
    var referrer: String?
    /// `claim_code`, already normalised.
    var claimCode: String?
    /// `login_key`, an opaque already-hashed account identifier.
    var loginKey: String?
    /// `signals`. `nil` means the member is absent from the body entirely (TC-145, TC-146).
    var signals: DeviceSignalsBody?
    /// `consent`.
    var consent: ConsentBody?

    private enum CodingKeys: String, CodingKey {
        case installId = "install_id"
        case platform
        case appVersion = "app_version"
        case osVersion = "os_version"
        case referrer
        case claimCode = "claim_code"
        case loginKey = "login_key"
        case signals
        case consent
    }

    func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(installId, forKey: .installId)
        try c.encode(platform, forKey: .platform)
        try c.encodeIfPresent(appVersion, forKey: .appVersion)
        try c.encodeIfPresent(osVersion, forKey: .osVersion)
        try c.encodeIfPresent(referrer.map { String($0.prefix(DleAPI.maxReferrerLength)) }, forKey: .referrer)
        try c.encodeIfPresent(claimCode, forKey: .claimCode)
        try c.encodeIfPresent(loginKey, forKey: .loginKey)
        try c.encodeIfPresent(signals, forKey: .signals)
        try c.encodeIfPresent(consent, forKey: .consent)
    }
}

/// `EventBatchDto`.
struct EventBatchBody: Encodable, Sendable, Hashable {
    /// `install_id`.
    var installId: String
    /// `platform`.
    var platform: String?
    /// `app_version`.
    var appVersion: String?
    /// `events`, 1 to `DleAPI.maxEventsPerBatch`.
    var events: [DleEvent]

    private enum CodingKeys: String, CodingKey {
        case installId = "install_id"
        case platform
        case appVersion = "app_version"
        case events
    }

    func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(installId, forKey: .installId)
        try c.encodeIfPresent(platform, forKey: .platform)
        try c.encodeIfPresent(appVersion, forKey: .appVersion)
        try c.encode(events, forKey: .events)
    }
}

/// `EventBatchAcceptedDto`.
struct EventBatchAccepted: Decodable, Sendable, Hashable {
    /// Events accepted for processing.
    let accepted: Int
    /// Events refused (unknown type, unusable timestamp).
    let rejected: Int

    private enum CodingKeys: String, CodingKey {
        case accepted, rejected
    }

    init(accepted: Int, rejected: Int) {
        self.accepted = accepted
        self.rejected = rejected
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        accepted = try c.decodeIfPresent(Int.self, forKey: .accepted) ?? 0
        rejected = try c.decodeIfPresent(Int.self, forKey: .rejected) ?? 0
    }
}

// MARK: - Client

/// The HTTP client of the SDK plane: `POST /v1/resolve` and `POST /v1/events` (spec §B.7.2).
///
/// Every request carries `Authorization: Bearer <sdkKey>` and a fresh W3C `traceparent`
/// (spec §C.6), and is JSON in the snake_case shape of `Dle.Domain.Contracts.SdkContracts`.
/// Failures become ``DleError`` values; the response status, never the body, is logged.
struct DleAPI: Sendable {
    /// `EventBatchDto.MaxEventsPerBatch`.
    static let maxEventsPerBatch = 100
    /// `InstallReferrerParser.MaxReferrerLength`.
    static let maxReferrerLength = 1024
    /// Path of the resolve endpoint.
    static let resolvePath = "/v1/resolve"
    /// Path of the events endpoint.
    static let eventsPath = "/v1/events"

    let endpoint: URL
    let sdkKey: String
    let transport: DleTransport
    let timeout: TimeInterval
    let clock: DleClock

    /// `POST /v1/resolve`. Returns the parsed body; a body without `match_type` or
    /// `confidence` is a ``DleError/malformedResponse(_:)``.
    func resolve(_ body: ResolveRequestBody) async throws -> DeferredLink {
        try await post(Self.resolvePath, body: body)
    }

    /// `POST /v1/events`. Resolves on 202 with the accepted and rejected counts.
    func sendEvents(_ body: EventBatchBody) async throws -> EventBatchAccepted {
        guard (1...Self.maxEventsPerBatch).contains(body.events.count) else {
            throw DleError.invalidBatch(count: body.events.count)
        }
        return try await post(Self.eventsPath, body: body)
    }

    private func post<Body: Encodable & Sendable, Out: Decodable & Sendable>(_ path: String, body: Body) async throws -> Out {
        let url = try Self.url(endpoint: endpoint, path: path)
        let data: Data
        do {
            data = try Self.makeEncoder().encode(body)
        } catch {
            throw DleError.malformedResponse("request body could not be encoded")
        }
        let request = Self.makeRequest(url: url, sdkKey: sdkKey, body: data, traceparent: Self.traceparent(), timeout: timeout)

        let responseData: Data
        let response: HTTPURLResponse
        do {
            (responseData, response) = try await transport.send(request)
        } catch let error as DleError {
            throw error
        } catch is CancellationError {
            throw CancellationError()
        } catch let error as URLError {
            switch error.code {
            case .cancelled:
                throw CancellationError()
            case .timedOut:
                throw DleError.timeout
            default:
                throw DleError.network("URLError(\(error.code.rawValue))")
            }
        } catch {
            throw DleError.network(String(describing: Swift.type(of: error)))
        }

        let status = response.statusCode
        if (200..<300).contains(status) {
            guard !responseData.isEmpty else {
                throw DleError.malformedResponse("HTTP \(status) without a body")
            }
            do {
                return try Self.makeDecoder().decode(Out.self, from: responseData)
            } catch {
                throw DleError.malformedResponse("HTTP \(status) body is not a valid \(Out.self)")
            }
        }

        // Status codes only — the key, the body and the problem detail never reach the log.
        DleLog.debug("POST \(path) -> \(status)")
        let retryAfter = Self.parseRetryAfter(response.value(forHTTPHeaderField: "Retry-After"), now: clock.now)
        let problem = responseData.isEmpty ? nil : try? Self.makeDecoder().decode(DleProblem.self, from: responseData)
        throw Self.classify(status: status, problem: problem, retryAfter: retryAfter)
    }

    /// Maps a non-success response onto a ``DleError``.
    static func classify(status: Int, problem: DleProblem?, retryAfter: TimeInterval?) -> DleError {
        if status == 400, let rejection = problem?.claimCodeRejection {
            let canReissue = problem?.canReissue ?? (rejection != .consumed && rejection != .disabled)
            return .claimCodeRejected(reason: rejection, canReissue: canReissue)
        }
        return .http(status: status, problem: problem, retryAfter: retryAfter)
    }

    /// Joins the endpoint and a path without doubling or dropping slashes.
    static func url(endpoint: URL, path: String) throws -> URL {
        var base = endpoint.absoluteString
        while base.hasSuffix("/") { base.removeLast() }
        guard let url = URL(string: base + path) else {
            throw DleError.invalidConfiguration("endpoint and path do not form a URL")
        }
        return url
    }

    /// Builds the request with the contract's headers.
    static func makeRequest(url: URL, sdkKey: String, body: Data, traceparent: String, timeout: TimeInterval) -> URLRequest {
        var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalAndRemoteCacheData, timeoutInterval: timeout)
        request.httpMethod = "POST"
        request.httpBody = body
        request.httpShouldHandleCookies = false
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json, application/problem+json", forHTTPHeaderField: "Accept")
        request.setValue("Bearer \(sdkKey)", forHTTPHeaderField: "Authorization")
        request.setValue(traceparent, forHTTPHeaderField: "traceparent")
        request.setValue("no-store", forHTTPHeaderField: "Cache-Control")
        request.setValue("DleSDK-iOS/\(Dle.version) (\(DlePlatform.wireName))", forHTTPHeaderField: "User-Agent")
        return request
    }

    /// A fresh W3C `traceparent` (version 00, sampled): random ids, never derived from anything.
    static func traceparent() -> String {
        var generator = SystemRandomNumberGenerator()
        var trace = (0..<16).map { _ in UInt8.random(in: .min ... .max, using: &generator) }
        var span = (0..<8).map { _ in UInt8.random(in: .min ... .max, using: &generator) }
        if trace.allSatisfy({ $0 == 0 }) { trace[15] = 1 }
        if span.allSatisfy({ $0 == 0 }) { span[7] = 1 }
        return "00-\(hex(trace))-\(hex(span))-01"
    }

    /// Parses `Retry-After` (delay-seconds or an HTTP-date) into seconds.
    static func parseRetryAfter(_ header: String?, now: Date) -> TimeInterval? {
        guard let header else { return nil }
        let trimmed = header.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }
        if trimmed.allSatisfy(\.isNumber), let seconds = TimeInterval(trimmed) {
            return seconds
        }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        formatter.dateFormat = "EEE, dd MMM yyyy HH:mm:ss zzz"
        guard let at = formatter.date(from: trimmed) else { return nil }
        return max(0, at.timeIntervalSince(now))
    }

    /// The encoder every wire body goes through. No key strategy: keys are explicit.
    static func makeEncoder() -> JSONEncoder {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.withoutEscapingSlashes]
        return encoder
    }

    /// The decoder every wire body goes through.
    static func makeDecoder() -> JSONDecoder {
        JSONDecoder()
    }

    private static func hex(_ bytes: [UInt8]) -> String {
        let digits = Array("0123456789abcdef")
        var out = ""
        out.reserveCapacity(bytes.count * 2)
        for byte in bytes {
            out.append(digits[Int(byte >> 4)])
            out.append(digits[Int(byte & 0x0f)])
        }
        return out
    }
}
