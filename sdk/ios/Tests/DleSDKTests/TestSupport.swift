import Foundation
import XCTest
@testable import DleSDK

// Fakes shared by the test files. Everything is deterministic: no wall clock, no randomness,
// no network, no Keychain, no shared directory.

/// A clock that only moves when a test moves it. `sleep` throws `CancellationError`
/// immediately, which makes `EventQueue`'s background worker exit before draining, so every
/// send in a test happens through an explicit `flush()` and nothing races.
final class FakeClock: DleClock, @unchecked Sendable {
    private let box: Locked<Date>

    init(now: Date = TestDates.moment) {
        box = Locked(now)
    }

    var now: Date { box.current }

    func advance(by seconds: TimeInterval) {
        box.withLock { $0 = $0.addingTimeInterval(seconds) }
    }

    func sleep(seconds: TimeInterval) async throws {
        throw CancellationError()
    }
}

/// Returns one fixed value.
struct FakeRandom: DleRandomSource {
    var value: Double = 0.5

    func unitRandom() -> Double { value }
}

/// A scripted HTTP transport. Every request is recorded; responses are consumed in order and,
/// once they run out, the transport behaves like a device with no connectivity.
final class StubTransport: DleTransport, @unchecked Sendable {
    struct Scripted: Sendable {
        var status: Int
        var body: String
        var headers: [String: String] = [:]
    }

    let requests = Locked<[URLRequest]>([])
    private let responses: Locked<[Scripted]>

    init(_ responses: [Scripted] = []) {
        self.responses = Locked(responses)
    }

    var requestCount: Int { requests.current.count }

    /// The JSON object of the n-th request body.
    func requestBody(_ index: Int = 0) throws -> [String: Any] {
        let request = requests.current[index]
        let data = try XCTUnwrap(request.httpBody)
        return try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        requests.withLock { $0.append(request) }
        let next: Scripted? = responses.withLock { $0.isEmpty ? nil : $0.removeFirst() }
        guard let next else { throw URLError(.notConnectedToInternet) }
        let http = HTTPURLResponse(
            url: request.url ?? URL(string: "https://invalid.test")!,
            statusCode: next.status,
            httpVersion: "HTTP/1.1",
            headerFields: next.headers)!
        return (Data(next.body.utf8), http)
    }
}

/// An in-memory Keychain, shared between the instances a test creates so that "relaunch"
/// scenarios can be written by constructing a second `Dle` over the same fakes.
final class MemoryKeychain: KeychainStorage, @unchecked Sendable {
    private let items = Locked<[String: String]>([:])

    func read(service: String, account: String) throws -> String? {
        items.current[service + "/" + account]
    }

    func write(_ value: String, service: String, account: String) throws {
        items.withLock { $0[service + "/" + account] = value }
    }

    func delete(service: String, account: String) throws {
        items.withLock { $0[service + "/" + account] = nil }
    }
}

enum TestDates {
    /// `2026-09-03T10:00:00+00:00`, the instant of every literal in
    /// `tests/Dle.ContractTests/Wire/SdkWireContractTests.cs`.
    static let moment: Date = {
        var components = DateComponents()
        components.year = 2026
        components.month = 9
        components.day = 3
        components.hour = 10
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        return calendar.date(from: components)!
    }()
}

enum TestSupport {
    /// The install id of the contract literals.
    static let installId = "9f2c1b64-0c3f-4a8e-9c2d-8f4c1f0a77b1"

    /// A fresh, empty storage directory under the temporary directory.
    static func makeStorage() throws -> DleStorage {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("DleSDKTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return DleStorage(directory: directory)
    }

    /// A configuration pointing at a test endpoint with link hosts set.
    static func makeConfig(
        consent: DleConsent = .denied(at: TestDates.moment),
        strategies: DleDeferredStrategies = .deterministic,
        linkHosts: [String] = ["links.example.test"]
    ) -> DleConfig {
        DleConfig(
            endpoint: URL(string: "https://links.example.test")!,
            sdkKey: "dle_pk_test",
            linkHosts: linkHosts,
            consent: consent,
            deferredStrategies: strategies,
            appVersion: "3.4.1",
            flushOnForeground: false,
            storageNamespace: "tests",
            logLevel: .off)
    }

    /// A facade over fakes.
    static func makeDle(
        transport: StubTransport,
        storage: DleStorage?,
        keychain: KeychainStorage = MemoryKeychain(),
        clock: FakeClock = FakeClock(),
        consent: DleConsent = .denied(at: TestDates.moment),
        strategies: DleDeferredStrategies = .deterministic,
        linkHosts: [String] = ["links.example.test"]
    ) throws -> Dle {
        try Dle(
            config: makeConfig(consent: consent, strategies: strategies, linkHosts: linkHosts),
            transport: transport,
            keychain: keychain,
            storage: storage,
            clock: clock,
            random: FakeRandom())
    }

    /// Re-serialises JSON with sorted keys so two documents can be compared byte for byte
    /// regardless of the member order their producers chose.
    static func canonical(_ json: String) throws -> String {
        try canonical(Data(json.utf8))
    }

    static func canonical(_ data: Data) throws -> String {
        let object = try JSONSerialization.jsonObject(with: data, options: [.fragmentsAllowed])
        let out = try JSONSerialization.data(withJSONObject: object, options: [.sortedKeys, .withoutEscapingSlashes])
        return String(decoding: out, as: UTF8.self)
    }

    static func encode<T: Encodable>(_ value: T) throws -> String {
        String(decoding: try DleAPI.makeEncoder().encode(value), as: UTF8.self)
    }

    static func decode<T: Decodable>(_ type: T.Type, _ json: String) throws -> T {
        try DleAPI.makeDecoder().decode(type, from: Data(json.utf8))
    }

    /// A matched, final, deterministic resolve response body.
    static let matchedClaimCodeResponse = """
        {"matched":true,"match_type":"claim_code","confidence":1.0,"click_id":"aB3xK9pQ","link":{"id":"7286414500000000001","deeplink_path":"/promo/jesen","campaign":"jesen26"},"params":{"promo":"AUTUMN20"},"expires_in":0}
        """

    /// The unmatched, final resolve response body of the contract tests.
    static let unmatchedResponse = """
        {"matched":false,"match_type":"none","confidence":0,"params":{},"expires_in":0}
        """
}
