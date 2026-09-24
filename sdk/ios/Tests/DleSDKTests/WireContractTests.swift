import XCTest
@testable import DleSDK

/// The SDK wire contract of §B.7.2, asserted against the literal bodies of
/// `tests/Dle.ContractTests/Wire/SdkWireContractTests.cs`.
///
/// The literals are copied, not re-derived: the point is that the bytes this SDK produces are
/// the bytes the engine's serializer produces for the same DTO, member name for member name.
/// Member order is compared canonically (sorted keys), because `JSONEncoder` on older Darwin
/// releases does not preserve encoding order; every name, value and rendering is byte-exact.
final class WireContractTests: XCTestCase {
    private let moment = TestDates.moment

    // MARK: - ResolveRequestDto

    func testResolveRequestSerializesToTheAndroidLiteralOfB72() throws {
        // Android literal, reproduced so the model is proven member-complete against the contract.
        let expected = """
            {"install_id":"9f2c1b64-0c3f-4a8e-9c2d-8f4c1f0a77b1","platform":"android","app_version":"3.4.1","os_version":"15","referrer":"dl_cid%3DaB3xK9pQ%26utm_source%3Dfb","signals":{"language":"sk-SK","screen":"1080x2400","tz_offset":120},"consent":{"analytics":true,"attribution":true,"ts":"2026-09-03T10:00:00+00:00"}}
            """
        let body = ResolveRequestBody(
            installId: TestSupport.installId,
            platform: "android",
            appVersion: "3.4.1",
            osVersion: "15",
            referrer: "dl_cid%3DaB3xK9pQ%26utm_source%3Dfb",
            claimCode: nil,
            loginKey: nil,
            signals: DeviceSignalsBody(language: "sk-SK", screen: "1080x2400", tzOffset: 120, deviceModel: nil),
            consent: ConsentBody(analytics: true, attribution: true, timestamp: moment))

        let json = try TestSupport.encode(body)

        XCTAssertEqual(try TestSupport.canonical(json), try TestSupport.canonical(expected))
        XCTAssertTrue(json.contains(#""ts":"2026-09-03T10:00:00+00:00""#), json)
        XCTAssertFalse(json.contains("claim_code"), "absent members are omitted, not null")
        XCTAssertFalse(json.contains("login_key"))
        XCTAssertFalse(json.contains("device_model"))
        XCTAssertFalse(json.contains("null"))
        XCTAssertFalse(json.contains("installId"), "camelCase is refused by the engine")
    }

    func testResolveRequestOnIosCarriesClaimCodeAndLoginKey() throws {
        let body = Dle.makeResolveBody(
            installId: TestSupport.installId,
            appVersion: "3.4.1",
            osVersion: "18.0.1",
            claimCode: "ACDEFG",
            loginKey: "h_9c2d",
            signals: nil,
            consent: DleConsent(analytics: false, attribution: true, timestamp: moment))

        let json = try TestSupport.encode(body)
        let expected = """
            {"install_id":"9f2c1b64-0c3f-4a8e-9c2d-8f4c1f0a77b1","platform":"\(DlePlatform.wireName)","app_version":"3.4.1","os_version":"18.0.1","claim_code":"ACDEFG","login_key":"h_9c2d","consent":{"analytics":false,"attribution":true,"ts":"2026-09-03T10:00:00+00:00"}}
            """
        XCTAssertEqual(try TestSupport.canonical(json), try TestSupport.canonical(expected))
        XCTAssertFalse(json.contains("referrer"), "no Install Referrer on Apple platforms")
        XCTAssertFalse(json.contains("signals"))
    }

    func testResolveRequestSignalsObjectIsAbsentNotEmptyWhenNil() throws {
        var body = Dle.makeResolveBody(
            installId: TestSupport.installId, appVersion: nil, osVersion: nil, claimCode: nil, loginKey: nil,
            signals: nil, consent: .denied(at: moment))
        XCTAssertFalse(try TestSupport.encode(body).contains("signals"))

        body.signals = DeviceSignalsBody(language: "sk-SK", screen: nil, tzOffset: 120, deviceModel: "iPhone15,2")
        let json = try TestSupport.encode(body)
        XCTAssertTrue(json.contains(#""signals":{"#), json)
        XCTAssertTrue(json.contains(#""tz_offset":120"#), json)
        XCTAssertTrue(json.contains(#""device_model":"iPhone15,2""#), json)
        XCTAssertFalse(json.contains("screen"))
    }

    // MARK: - ResolveResponseDto

    func testResolveResponseDecodesTheLiteralOfB72() throws {
        let literal = """
            {"matched":true,"match_type":"install_referrer","confidence":1.0,"click_id":"aB3xK9pQ","link":{"id":"7286414500000000001","deeplink_path":"/promo/jesen","campaign":"jesen26"},"params":{"utm_source":"fb","utm_campaign":"jesen26","promo":"AUTUMN20"},"expires_in":0}
            """
        let link = try TestSupport.decode(DeferredLink.self, literal)

        XCTAssertTrue(link.matched)
        XCTAssertEqual(link.matchType, .installReferrer)
        XCTAssertEqual(link.confidence, 1.0)
        XCTAssertEqual(link.clickId, "aB3xK9pQ")
        XCTAssertEqual(link.link?.id, "7286414500000000001")
        XCTAssertEqual(link.link?.deeplinkPath, "/promo/jesen")
        XCTAssertEqual(link.link?.campaign, "jesen26")
        XCTAssertNil(link.link?.title)
        XCTAssertEqual(link.params, ["utm_source": "fb", "utm_campaign": "jesen26", "promo": "AUTUMN20"])
        XCTAssertEqual(link.expiresIn, 0)
        XCTAssertTrue(link.isFinal)
        XCTAssertTrue(link.isDeterministic)
    }

    func testUnmatchedResponseHasNoContextAndIsNotMalformed() throws {
        let link = try TestSupport.decode(DeferredLink.self, TestSupport.unmatchedResponse)
        XCTAssertFalse(link.matched)
        XCTAssertEqual(link.matchType, DleMatchType.none)
        XCTAssertEqual(link.confidence, 0)
        XCTAssertNil(link.clickId)
        XCTAssertNil(link.link)
        XCTAssertEqual(link.params, [:])
        XCTAssertFalse(link.isDeterministic)
    }

    func testParamsKeysKeepTheirOriginalSpelling() throws {
        let link = try TestSupport.decode(
            DeferredLink.self,
            #"{"matched":true,"match_type":"direct_open","confidence":1.0,"params":{"utmSource":"fb","PromoCode":"AUTUMN20"},"expires_in":0}"#)
        XCTAssertEqual(link.params["utmSource"], "fb")
        XCTAssertEqual(link.params["PromoCode"], "AUTUMN20")
    }

    func testDecodingToleratesUnknownMembers() throws {
        let link = try TestSupport.decode(
            DeferredLink.self,
            #"{"matched":true,"match_type":"login","confidence":1,"future_member":{"x":[1,2]},"link":{"id":"1","future":true},"expires_in":0}"#)
        XCTAssertEqual(link.matchType, .login)
        XCTAssertEqual(link.link?.id, "1")
    }

    func testDecodingRefusesABodyWithoutMatchTypeOrConfidence() {
        XCTAssertThrowsError(try TestSupport.decode(DeferredLink.self, #"{"matched":true,"confidence":1.0}"#))
        XCTAssertThrowsError(try TestSupport.decode(DeferredLink.self, #"{"matched":true,"match_type":"login"}"#))
    }

    func testEveryMatchTypeDecodesFromItsWireName() throws {
        let wire: [DleMatchType: String] = [
            DleMatchType.none: "none",
            .installReferrer: "install_referrer",
            .login: "login",
            .claimCode: "claim_code",
            .probabilistic: "probabilistic",
            .directOpen: "direct_open",
        ]
        XCTAssertEqual(Set(wire.keys), Set(DleMatchType.allCases))
        for (type, name) in wire {
            XCTAssertEqual(type.rawValue, name)
            let link = try TestSupport.decode(
                DeferredLink.self, #"{"matched":true,"match_type":"\#(name)","confidence":0.9,"expires_in":0}"#)
            XCTAssertEqual(link.matchType, type)
        }
        let unknown = try TestSupport.decode(
            DeferredLink.self, #"{"matched":true,"match_type":"telepathy","confidence":1,"expires_in":0}"#)
        XCTAssertEqual(unknown.matchType, DleMatchType.none, "an unknown strategy is 'not attributed', never 'attributed somehow'")
        XCTAssertFalse(DleMatchType.reachableOnIOS.contains(.installReferrer))
    }

    func testDeferredLinkRoundTripsThroughPersistence() throws {
        let original = try TestSupport.decode(DeferredLink.self, TestSupport.matchedClaimCodeResponse)
        let json = try TestSupport.encode(original)
        let restored = try TestSupport.decode(DeferredLink.self, json)
        XCTAssertEqual(original, restored)
        XCTAssertTrue(json.contains(#""match_type":"claim_code""#), json)
        XCTAssertTrue(json.contains(#""deeplink_path":"/promo/jesen""#), "slashes are not escaped: \(json)")
    }

    // MARK: - EventBatchDto

    func testEventBatchSerializesToTheLiteralOfB72() throws {
        let expected = """
            {"install_id":"9f2c1b64-0c3f-4a8e-9c2d-8f4c1f0a77b1","events":[{"type":"link_open","url":"https://link.zak.sk/aB3xK9pQ","ts":"2026-09-03T10:00:00+00:00"},{"type":"conversion","name":"purchase","value":24.9,"currency":"EUR","ts":"2026-09-03T10:05:00+00:00"}]}
            """
        let batch = EventBatchBody(
            installId: TestSupport.installId,
            platform: nil,
            appVersion: nil,
            events: [
                DleEvent(type: .linkOpen, url: "https://link.zak.sk/aB3xK9pQ", timestamp: moment),
                DleEvent(type: .conversion, name: "purchase", value: 24.9, currency: "EUR", timestamp: moment.addingTimeInterval(300)),
            ])

        let json = try TestSupport.encode(batch)

        XCTAssertEqual(try TestSupport.canonical(json), try TestSupport.canonical(expected))
        XCTAssertTrue(json.contains(#""url":"https://link.zak.sk/aB3xK9pQ""#), "slashes must not be escaped: \(json)")
        XCTAssertTrue(json.contains(#""ts":"2026-09-03T10:05:00+00:00""#), json)
        XCTAssertTrue(json.contains(#""value":24.9"#), json)
        XCTAssertFalse(json.contains("timestamp"), "the wire name is ts")
        XCTAssertFalse(json.contains("null"))
    }

    func testEventTypesUseTheirWireNames() {
        XCTAssertEqual(DleEventType.linkOpen.rawValue, "link_open")
        XCTAssertEqual(DleEventType.firstOpen.rawValue, "first_open")
        XCTAssertEqual(DleEventType.session.rawValue, "session")
        XCTAssertEqual(DleEventType.conversion.rawValue, "conversion")
        XCTAssertEqual(DleEventType.custom.rawValue, "custom")
    }

    func testEventBatchAcceptedDecodesItsTwoCounters() throws {
        let accepted = try TestSupport.decode(EventBatchAccepted.self, #"{"accepted":2,"rejected":0}"#)
        XCTAssertEqual(accepted.accepted, 2)
        XCTAssertEqual(accepted.rejected, 0)
        XCTAssertEqual(DleAPI.maxEventsPerBatch, 100)
    }

    // MARK: - Problems and errors

    func testClaimCodeProblemBecomesClaimCodeRejected() throws {
        let problem = try TestSupport.decode(
            DleProblem.self,
            #"{"type":"https://docs.dle.dev/problems/claim-code-invalid","title":"Claim code invalid","status":400,"reason":"expired","can_reissue":true,"unknown":1}"#)
        XCTAssertEqual(problem.claimCodeRejection, .expired)
        XCTAssertEqual(problem.canReissue, true)

        let error = DleAPI.classify(status: 400, problem: problem, retryAfter: nil)
        XCTAssertEqual(error, .claimCodeRejected(reason: .expired, canReissue: true))
        XCTAssertFalse(error.isRetriable)
    }

    func testRateLimitedProblemCarriesRetryAfter() throws {
        let problem = try TestSupport.decode(
            DleProblem.self, #"{"type":"https://docs.dle.dev/problems/rate-limited","status":429}"#)
        let error = DleAPI.classify(status: 429, problem: problem, retryAfter: 30)
        XCTAssertEqual(error, .http(status: 429, problem: problem, retryAfter: 30))
        XCTAssertTrue(error.isRetriable)
        XCTAssertEqual(error.retryAfter, 30)
    }

    func testRetryAfterParsesSecondsAndHttpDates() {
        XCTAssertEqual(DleAPI.parseRetryAfter("30", now: moment), 30)
        XCTAssertEqual(DleAPI.parseRetryAfter(" 7 ", now: moment), 7)
        XCTAssertNil(DleAPI.parseRetryAfter(nil, now: moment))
        XCTAssertNil(DleAPI.parseRetryAfter("soon", now: moment))
        // Thu, 03 Sep 2026 10:00:10 GMT is ten seconds after `moment`.
        XCTAssertEqual(DleAPI.parseRetryAfter("Thu, 03 Sep 2026 10:00:10 GMT", now: moment), 10)
        XCTAssertEqual(DleAPI.parseRetryAfter("Thu, 03 Sep 2026 09:00:00 GMT", now: moment), 0, "a date in the past is 'now'")
    }

    // MARK: - Request shape

    func testRequestCarriesBearerJsonAndTraceparent() throws {
        let request = DleAPI.makeRequest(
            url: try DleAPI.url(endpoint: URL(string: "https://links.example.test/")!, path: DleAPI.resolvePath),
            sdkKey: "dle_pk_test",
            body: Data("{}".utf8),
            traceparent: DleAPI.traceparent(),
            timeout: 10)

        XCTAssertEqual(request.url?.absoluteString, "https://links.example.test/v1/resolve")
        XCTAssertEqual(request.httpMethod, "POST")
        XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer dle_pk_test")
        XCTAssertEqual(request.value(forHTTPHeaderField: "Content-Type"), "application/json")
        let traceparent = try XCTUnwrap(request.value(forHTTPHeaderField: "traceparent"))
        XCTAssertNotNil(traceparent.range(of: "^00-[0-9a-f]{32}-[0-9a-f]{16}-01$", options: .regularExpression), traceparent)
        XCTAssertNotEqual(DleAPI.traceparent(), DleAPI.traceparent(), "every request gets fresh ids")
        XCTAssertTrue(try XCTUnwrap(request.value(forHTTPHeaderField: "User-Agent")).hasPrefix("DleSDK-iOS/\(Dle.version)"))
    }

    func testEventsEndpointAccepts202() async throws {
        let transport = StubTransport([.init(status: 202, body: #"{"accepted":2,"rejected":0}"#)])
        let api = DleAPI(endpoint: URL(string: "https://links.example.test")!, sdkKey: "k", transport: transport, timeout: 10, clock: FakeClock())

        let result = try await api.sendEvents(EventBatchBody(
            installId: TestSupport.installId, platform: "ios", appVersion: nil,
            events: [DleEvent(type: .linkOpen, url: "https://links.example.test/x", timestamp: moment), .session(at: moment)]))

        XCTAssertEqual(result.accepted, 2)
        XCTAssertEqual(transport.requests.current.first?.url?.path, "/v1/events")
        let body = try transport.requestBody()
        XCTAssertEqual(body["install_id"] as? String, TestSupport.installId)
        XCTAssertEqual(body["platform"] as? String, "ios")
        XCTAssertEqual((body["events"] as? [[String: Any]])?.count, 2)
    }

    func testEventsEndpoint429SurfacesRetryAfterAndIsRetriable() async throws {
        let transport = StubTransport([
            .init(status: 429, body: #"{"type":"https://docs.dle.dev/problems/rate-limited","status":429}"#, headers: ["Retry-After": "7"]),
        ])
        let api = DleAPI(endpoint: URL(string: "https://links.example.test")!, sdkKey: "k", transport: transport, timeout: 10, clock: FakeClock())

        do {
            _ = try await api.sendEvents(EventBatchBody(installId: "a", platform: nil, appVersion: nil, events: [.session(at: moment)]))
            XCTFail("expected an error")
        } catch let error as DleError {
            XCTAssertEqual(error.retryAfter, 7)
            XCTAssertTrue(error.isRetriable)
            XCTAssertEqual(error.problem?.type, DleProblem.rateLimited)
        }
    }

    func testEventsEndpointRefusesAnEmptyOrOversizedBatchLocally() async {
        let api = DleAPI(endpoint: URL(string: "https://links.example.test")!, sdkKey: "k", transport: StubTransport(), timeout: 10, clock: FakeClock())
        do {
            _ = try await api.sendEvents(EventBatchBody(installId: "a", platform: nil, appVersion: nil, events: []))
            XCTFail("expected an error")
        } catch let error as DleError {
            XCTAssertEqual(error, .invalidBatch(count: 0))
        } catch {
            XCTFail("unexpected \(error)")
        }
    }

    func testResolveEndpointReturnsTheDecodedBody() async throws {
        let transport = StubTransport([.init(status: 200, body: TestSupport.matchedClaimCodeResponse)])
        let api = DleAPI(endpoint: URL(string: "https://links.example.test")!, sdkKey: "k", transport: transport, timeout: 10, clock: FakeClock())

        let link = try await api.resolve(Dle.makeResolveBody(
            installId: TestSupport.installId, appVersion: nil, osVersion: nil, claimCode: "ACDEFG", loginKey: nil,
            signals: nil, consent: .denied(at: moment)))

        XCTAssertEqual(link.matchType, .claimCode)
        XCTAssertEqual(transport.requests.current.first?.url?.path, "/v1/resolve")
        XCTAssertEqual(try transport.requestBody()["claim_code"] as? String, "ACDEFG")
    }
}
