import XCTest
@testable import DleSDK

/// Direct opens (FR-223): the URLs iOS hands the app become `link_open` events — only the
/// `http(s)` ones on the configured hosts, and never a custom-scheme URL.
final class UniversalLinkTests: XCTestCase {
    func testHostPatternsAreNormalisedAndMatched() {
        XCTAssertEqual(DleLinkPolicy.normalizeHostPattern(" Links.Example.SK. "), "links.example.sk")
        XCTAssertEqual(DleLinkPolicy.normalizeHostPattern("*.example.sk"), "*.example.sk")
        XCTAssertNil(DleLinkPolicy.normalizeHostPattern("https://links.example.sk"))
        XCTAssertNil(DleLinkPolicy.normalizeHostPattern("links.example.sk/path"))
        XCTAssertNil(DleLinkPolicy.normalizeHostPattern("links.example.sk:443"))
        XCTAssertNil(DleLinkPolicy.normalizeHostPattern(""))
        XCTAssertNil(DleLinkPolicy.normalizeHostPattern("*"))
        XCTAssertNil(DleLinkPolicy.normalizeHostPattern("**.example.sk"))

        let patterns = ["links.example.sk", "*.go.example.sk"]
        XCTAssertTrue(DleLinkPolicy.matches(host: "links.example.sk", patterns: patterns))
        XCTAssertTrue(DleLinkPolicy.matches(host: "LINKS.EXAMPLE.SK", patterns: patterns))
        XCTAssertTrue(DleLinkPolicy.matches(host: "a.go.example.sk", patterns: patterns))
        XCTAssertFalse(DleLinkPolicy.matches(host: "go.example.sk", patterns: patterns), "a wildcard does not match the apex")
        XCTAssertFalse(DleLinkPolicy.matches(host: "a.b.go.example.sk", patterns: patterns), "one level only")
        XCTAssertFalse(DleLinkPolicy.matches(host: "evil-links.example.sk", patterns: patterns))
        XCTAssertFalse(DleLinkPolicy.matches(host: "links.example.sk.evil.test", patterns: patterns))
    }

    func testWebLinkOnAConfiguredHostIsReportedWithoutItsFragment() throws {
        let dle = try TestSupport.makeDle(transport: StubTransport(), storage: nil)
        let url = URL(string: "https://links.example.test/aB3xK9pQ?utm_source=fb#state")!

        let routed = dle.handle(url: url)

        XCTAssertEqual(routed, url, "the host app routes the URL as delivered")
        XCTAssertEqual(dle.queue.events.map(\.type), [.linkOpen])
        XCTAssertEqual(dle.queue.events.first?.url, "https://links.example.test/aB3xK9pQ?utm_source=fb")
    }

    func testCustomSchemeURLIsReturnedButNeverReported() throws {
        let dle = try TestSupport.makeDle(transport: StubTransport(), storage: nil)
        let url = URL(string: "sampleapp://open?token=do-not-send")!

        XCTAssertEqual(dle.handle(url: url), url)
        XCTAssertEqual(dle.pendingEventCount, 0)
    }

    func testForeignHostIsReturnedButNotReported() throws {
        let dle = try TestSupport.makeDle(transport: StubTransport(), storage: nil)
        let url = URL(string: "https://other.example/x")!

        XCTAssertEqual(dle.handle(url: url), url)
        XCTAssertEqual(dle.pendingEventCount, 0)
    }

    func testEmptyLinkHostsReportsNothing() throws {
        let dle = try TestSupport.makeDle(transport: StubTransport(), storage: nil, linkHosts: [])
        _ = dle.handle(url: URL(string: "https://links.example.test/aB3xK9pQ")!)
        XCTAssertEqual(dle.pendingEventCount, 0)
    }

    func testBrowsingWebActivityIsHandledAndOthersAreNot() throws {
        let dle = try TestSupport.makeDle(transport: StubTransport(), storage: nil)
        let url = URL(string: "https://links.example.test/aB3xK9pQ")!

        let web = NSUserActivity(activityType: DleUniversalLink.browsingWebActivityType)
        web.webpageURL = url
        XCTAssertEqual(dle.handle(web), url)
        XCTAssertEqual(dle.pendingEventCount, 1)

        let other = NSUserActivity(activityType: "com.example.handoff")
        other.webpageURL = url
        XCTAssertNil(dle.handle(other), "not a Universal Link continuation")
        XCTAssertEqual(dle.pendingEventCount, 1)

        XCTAssertEqual(dle.handle(userActivities: [other, web]), url)
        XCTAssertEqual(dle.pendingEventCount, 2)
    }

    func testLinkOpenIsNotGatedOnConsent() throws {
        let dle = try TestSupport.makeDle(transport: StubTransport(), storage: nil, consent: .denied(at: TestDates.moment))
        dle.handle(url: URL(string: "https://links.example.test/aB3xK9pQ")!)
        XCTAssertEqual(dle.queue.events.map(\.type), [.linkOpen])
    }
}
