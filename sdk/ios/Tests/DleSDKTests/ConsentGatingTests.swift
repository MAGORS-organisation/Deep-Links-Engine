import XCTest
@testable import DleSDK

/// Consent is an input to the pipeline, not a filter after the fact (spec §E.6.2): with
/// attribution consent absent the `signals` object is not in the body at all (TC-145, TC-146),
/// and with analytics consent absent behavioural events are never queued.
final class ConsentGatingTests: XCTestCase {
    private let moment = TestDates.moment

    func testSignalsRequireBothTheStrategyAndConsent() {
        XCTAssertFalse(Dle.shouldAttachSignals(strategies: .deterministic, consent: .denied(at: moment)))
        XCTAssertFalse(Dle.shouldAttachSignals(strategies: .deterministic, consent: .granted(at: moment)), "consent alone does not turn collection on")
        XCTAssertFalse(Dle.shouldAttachSignals(strategies: [.probabilistic], consent: .denied(at: moment)), "the strategy alone does not either")
        XCTAssertFalse(Dle.shouldAttachSignals(strategies: [.probabilistic], consent: .analyticsOnly(at: moment)), "analytics consent is not attribution consent")
        XCTAssertTrue(Dle.shouldAttachSignals(strategies: [.probabilistic], consent: .granted(at: moment)))
        XCTAssertTrue(Dle.shouldAttachSignals(strategies: [.probabilistic], consent: DleConsent(analytics: false, attribution: true, timestamp: moment)))
    }

    func testResolveOmitsTheSignalsObjectWhenConsentIsDenied() async throws {
        let transport = StubTransport([.init(status: 200, body: TestSupport.unmatchedResponse)])
        let dle = try TestSupport.makeDle(
            transport: transport, storage: nil,
            consent: .denied(at: moment), strategies: [.claimCode, .login, .probabilistic])

        _ = try await dle.resolve()

        let body = try transport.requestBody()
        XCTAssertNil(body["signals"], "omitted entirely, not sent empty")
        XCTAssertEqual(body["install_id"] as? String, dle.installId)
        XCTAssertEqual(body["platform"] as? String, DlePlatform.wireName)
        XCTAssertEqual(body["app_version"] as? String, "3.4.1")
        XCTAssertEqual(body["os_version"] as? String, DlePlatform.osVersion())
        XCTAssertNil(body["referrer"])
        XCTAssertNil(body["claim_code"])
        let consent = try XCTUnwrap(body["consent"] as? [String: Any])
        XCTAssertEqual(consent["analytics"] as? Bool, false)
        XCTAssertEqual(consent["attribution"] as? Bool, false)
        XCTAssertEqual(consent["ts"] as? String, "2026-09-03T10:00:00+00:00")
    }

    func testResolveOmitsTheSignalsObjectWhenProbabilisticIsOff() async throws {
        let transport = StubTransport([.init(status: 200, body: TestSupport.unmatchedResponse)])
        let dle = try TestSupport.makeDle(
            transport: transport, storage: nil, consent: .granted(at: moment), strategies: .deterministic)

        _ = try await dle.resolve()

        let body = try transport.requestBody()
        XCTAssertNil(body["signals"])
        XCTAssertEqual((body["consent"] as? [String: Any])?["attribution"] as? Bool, true)
    }

    func testResolveAttachesSignalsOnlyWithStrategyAndConsent() async throws {
        let transport = StubTransport([.init(status: 200, body: TestSupport.unmatchedResponse)])
        let dle = try TestSupport.makeDle(
            transport: transport, storage: nil, consent: .granted(at: moment), strategies: [.claimCode, .probabilistic])

        _ = try await dle.resolve()

        let signals = try XCTUnwrap(try transport.requestBody()["signals"] as? [String: Any])
        XCTAssertNotNil(signals["tz_offset"] as? Int)
        XCTAssertEqual(signals["tz_offset"] as? Int, TimeZone.current.secondsFromGMT() / 60)
        for key in signals.keys {
            XCTAssertTrue(["language", "screen", "tz_offset", "device_model"].contains(key), "unexpected signal \(key)")
        }
    }

    func testConsentChangeAppliesToTheNextResolve() async throws {
        let transport = StubTransport([.init(status: 200, body: TestSupport.unmatchedResponse)])
        let dle = try TestSupport.makeDle(
            transport: transport, storage: nil, consent: .denied(at: moment), strategies: [.probabilistic, .claimCode])

        dle.updateConsent(.granted(at: moment))
        _ = try await dle.resolve()

        XCTAssertNotNil(try transport.requestBody()["signals"])
    }

    func testBehaviouralEventsAreGatedOnAnalyticsConsent() throws {
        let dle = try TestSupport.makeDle(transport: StubTransport(), storage: nil, consent: .denied(at: moment))
        XCTAssertEqual(dle.pendingEventCount, 0, "first_open waits for consent")

        dle.track(.custom(name: "tapped"))
        dle.track(.session())
        dle.track(.conversion(name: "purchase", value: 1, currency: "EUR"))
        XCTAssertEqual(dle.pendingEventCount, 0)

        dle.track(.linkOpen(url: URL(string: "https://links.example.test/aB3xK9pQ")!))
        XCTAssertEqual(dle.pendingEventCount, 1, "link_open is a first-party observation and is never gated")

        dle.updateConsent(.granted(at: moment))
        XCTAssertEqual(dle.queue.events.map(\.type), [.linkOpen, .firstOpen])

        dle.track(.custom(name: "tapped"))
        XCTAssertEqual(dle.pendingEventCount, 3)
    }

    func testWithdrawingAnalyticsConsentDiscardsQueuedBehaviouralEvents() throws {
        let dle = try TestSupport.makeDle(transport: StubTransport(), storage: nil, consent: .granted(at: moment))
        dle.track(.custom(name: "tapped"))
        dle.track(.linkOpen(url: URL(string: "https://links.example.test/aB3xK9pQ")!))
        XCTAssertEqual(dle.queue.events.map(\.type), [.firstOpen, .custom, .linkOpen])

        dle.setConsent(.denied(at: moment))

        XCTAssertEqual(dle.queue.events.map(\.type), [.linkOpen])
        XCTAssertEqual(dle.consent, .denied(at: moment))
    }

    func testFirstOpenIsEmittedOncePerInstallation() throws {
        let storage = try TestSupport.makeStorage()
        let keychain = MemoryKeychain()
        let first = try TestSupport.makeDle(transport: StubTransport(), storage: storage, keychain: keychain, consent: .granted(at: moment))
        XCTAssertEqual(first.queue.events.map(\.type), [.firstOpen])

        first.updateConsent(.granted(at: moment))
        XCTAssertEqual(first.queue.events.map(\.type), [.firstOpen], "not repeated on a consent update")
        first.shutdown()

        // A relaunch (same storage, same Keychain) loads the persisted queue and adds nothing.
        let second = try TestSupport.makeDle(transport: StubTransport(), storage: storage, keychain: keychain, consent: .granted(at: moment))
        XCTAssertEqual(second.queue.events.map(\.type), [.firstOpen])
        XCTAssertTrue(second.stateStore.current.firstOpenTracked)
    }
}
