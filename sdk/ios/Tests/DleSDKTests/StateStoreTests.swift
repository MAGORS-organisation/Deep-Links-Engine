import XCTest
@testable import DleSDK

/// `StateStore` persistence, and the rule it exists for: one resolve per installation (TC-143).
final class StateStoreTests: XCTestCase {
    private let moment = TestDates.moment

    // MARK: - Store

    func testStartsEmptyAndWritesTheInstallMarkerOnReset() throws {
        let storage = try TestSupport.makeStorage()
        let store = StateStore(storage: storage)

        XCTAssertTrue(store.isAvailable)
        XCTAssertFalse(store.exists(), "no state file before the first launch completes")
        XCTAssertNil(store.deferredLink)
        XCTAssertFalse(store.current.firstOpenTracked)

        store.write()
        XCTAssertTrue(storage.exists(StateStore.fileName))
        XCTAssertTrue(StateStore(storage: storage).exists(), "the marker is what tells an update from a reinstall")
    }

    func testPersistsTheResolveResultAndFlagsAcrossInstances() throws {
        let storage = try TestSupport.makeStorage()
        let link = try TestSupport.decode(DeferredLink.self, TestSupport.matchedClaimCodeResponse)

        StateStore(storage: storage).update { state in
            state.deferredLink = link
            state.resolvedAt = moment
            state.firstOpenTracked = true
        }

        let reloaded = StateStore(storage: storage).current
        XCTAssertEqual(reloaded.deferredLink, link)
        XCTAssertEqual(reloaded.resolvedAt, moment)
        XCTAssertTrue(reloaded.firstOpenTracked)
        XCTAssertEqual(reloaded.schema, 1)
    }

    func testCorruptStateIsTreatedAsEmptyButStillMarksTheInstall() throws {
        let storage = try TestSupport.makeStorage()
        try storage.write(Data("{\"deferred_link\":42,\"first_open_tracked\":\"yes\"".utf8), to: StateStore.fileName)

        let store = StateStore(storage: storage)

        XCTAssertTrue(store.exists())
        XCTAssertNil(store.deferredLink)
        XCTAssertFalse(store.current.firstOpenTracked)
    }

    func testWorksInMemoryWithoutStorage() {
        let store = StateStore(storage: nil)
        XCTAssertFalse(store.isAvailable)
        store.update { $0.firstOpenTracked = true }
        XCTAssertTrue(store.current.firstOpenTracked)
        store.reset()
        XCTAssertFalse(store.current.firstOpenTracked)
    }

    // MARK: - Resolve once (TC-143)

    func testResolveHappensOncePerInstallation() async throws {
        let storage = try TestSupport.makeStorage()
        let keychain = MemoryKeychain()
        let transport = StubTransport([.init(status: 200, body: TestSupport.matchedClaimCodeResponse)])
        let first = try TestSupport.makeDle(transport: transport, storage: storage, keychain: keychain)

        let a = try await first.resolve()
        let b = try await first.resolve()

        XCTAssertEqual(transport.requestCount, 1, "the second call is answered from the persisted result")
        XCTAssertEqual(a, b)
        XCTAssertTrue(first.isResolved)
        XCTAssertEqual(first.deferredLink, a)
        first.shutdown()

        // Relaunch: same storage and Keychain, a transport that would fail if it were used.
        let second = try TestSupport.makeDle(transport: StubTransport(), storage: storage, keychain: keychain)
        let c = try await second.resolve()

        XCTAssertEqual(c, a)
        XCTAssertEqual(second.installId, first.installId)
    }

    func testConcurrentResolvesProduceOneRequest() async throws {
        let transport = StubTransport([.init(status: 200, body: TestSupport.matchedClaimCodeResponse)])
        let dle = try TestSupport.makeDle(transport: transport, storage: nil)

        async let a = dle.resolve()
        async let b = dle.resolve()
        let (first, second) = try await (a, b)

        XCTAssertEqual(first, second)
        XCTAssertEqual(transport.requestCount, 1)
    }

    func testSupplementalClaimCodeIsSentAfterAnUnmatchedFirstResolve() async throws {
        let transport = StubTransport([
            .init(status: 200, body: TestSupport.unmatchedResponse),
            .init(status: 200, body: TestSupport.matchedClaimCodeResponse),
        ])
        let dle = try TestSupport.makeDle(transport: transport, storage: nil)

        let initial = try await dle.resolve()
        XCTAssertFalse(initial.matched)

        let claimed = try await dle.resolve(claimCode: " acd-efg ")
        XCTAssertEqual(claimed.matchType, .claimCode)
        XCTAssertEqual(transport.requestCount, 2)
        XCTAssertEqual(try transport.requestBody(1)["claim_code"] as? String, "ACDEFG", "normalised before sending")
        XCTAssertEqual(dle.deferredLink, claimed, "the deterministic answer replaces the unmatched one")

        _ = try await dle.submitClaimCode("ACDEFG")
        XCTAssertEqual(transport.requestCount, 2, "nothing to add once the answer is deterministic")
    }

    func testMalformedClaimCodeIsRefusedLocallyAndNeverSent() async throws {
        let transport = StubTransport()
        let dle = try TestSupport.makeDle(transport: transport, storage: nil)

        do {
            _ = try await dle.resolve(claimCode: "ABCDEF")
            XCTFail("expected claimCodeMalformed")
        } catch let error as DleError {
            XCTAssertEqual(error, .claimCodeMalformed)
        }
        XCTAssertEqual(transport.requestCount, 0)
        XCTAssertNil(dle.deferredLink)
    }

    func testDisabledStrategyIsRefusedLocally() async throws {
        let transport = StubTransport()
        let dle = try TestSupport.makeDle(transport: transport, storage: nil, strategies: [.claimCode])

        do {
            _ = try await dle.reconcileLogin(loginKey: "h_9c2d")
            XCTFail("expected strategyDisabled")
        } catch let error as DleError {
            XCTAssertEqual(error, .strategyDisabled(.login))
        }
        XCTAssertEqual(transport.requestCount, 0)
    }

    func testClaimCodeRejectionIsSurfacedAndNothingIsPersisted() async throws {
        let transport = StubTransport([
            .init(status: 400, body: #"{"type":"https://docs.dle.dev/problems/claim-code-invalid","status":400,"reason":"consumed","can_reissue":false}"#),
        ])
        let dle = try TestSupport.makeDle(transport: transport, storage: nil)

        do {
            _ = try await dle.submitClaimCode("ACDEFG")
            XCTFail("expected claimCodeRejected")
        } catch let error as DleError {
            XCTAssertEqual(error, .claimCodeRejected(reason: .consumed, canReissue: false))
        }
        XCTAssertNil(dle.deferredLink)
        XCTAssertFalse(dle.isResolved)
    }

    func testNonFinalResultIsRequestedAgainOnlyAfterItExpires() async throws {
        let clock = FakeClock()
        let transport = StubTransport([
            .init(status: 200, body: #"{"matched":false,"match_type":"none","confidence":0,"params":{},"expires_in":60}"#),
            .init(status: 200, body: TestSupport.unmatchedResponse),
        ])
        let dle = try TestSupport.makeDle(transport: transport, storage: nil, clock: clock)

        let pending = try await dle.resolve()
        XCTAssertFalse(pending.isFinal)
        _ = try await dle.resolve()
        XCTAssertEqual(transport.requestCount, 1, "still valid")

        clock.advance(by: 61)
        let final = try await dle.resolve()
        XCTAssertEqual(transport.requestCount, 2)
        XCTAssertTrue(final.isFinal)

        clock.advance(by: 3600)
        _ = try await dle.resolve()
        XCTAssertEqual(transport.requestCount, 2, "a final result is never re-requested")
    }

    func testInstallIdSurvivesAnUpdateAndRotatesOnReinstall() throws {
        let keychain = MemoryKeychain()
        let storageA = try TestSupport.makeStorage()
        let launch1 = try TestSupport.makeDle(transport: StubTransport(), storage: storageA, keychain: keychain)
        let launch2 = try TestSupport.makeDle(transport: StubTransport(), storage: storageA, keychain: keychain)
        XCTAssertEqual(launch1.installId, launch2.installId, "an update keeps the container and the id")
        XCTAssertTrue(InstallIdStore.isValid(launch1.installId))

        // A reinstall keeps the Keychain item but loses the container: the marker is gone.
        let storageB = try TestSupport.makeStorage()
        let reinstalled = try TestSupport.makeDle(transport: StubTransport(), storage: storageB, keychain: keychain)
        XCTAssertNotEqual(reinstalled.installId, launch1.installId, "a reinstalled app is a new installation")
        XCTAssertNil(reinstalled.deferredLink)
    }
}
