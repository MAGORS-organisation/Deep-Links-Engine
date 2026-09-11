import XCTest
@testable import DleSDK

/// `EventQueue` under a fake clock and fake randomness: the cap, the 100-event batches, the
/// back-off, `Retry-After`, and persistence. `FakeClock.sleep` throws, so the queue's own
/// worker never drains and every send below is the result of an explicit `flush()`.
final class EventQueueTests: XCTestCase {
    /// Records every batch and throws the scripted failures in order.
    private final class SenderSpy: @unchecked Sendable {
        let batches = Locked<[[DleEvent]]>([])
        let failures: Locked<[DleError]>

        init(failures: [DleError] = []) {
            self.failures = Locked(failures)
        }

        var sender: EventSender {
            { [self] events in
                self.batches.withLock { $0.append(events) }
                let failure: DleError? = self.failures.withLock { $0.isEmpty ? nil : $0.removeFirst() }
                if let failure { throw failure }
            }
        }
    }

    private func makeQueue(
        spy: SenderSpy,
        clock: FakeClock,
        storage: DleStorage? = nil,
        maxSize: Int = 200,
        random: Double = 0.5
    ) -> EventQueue {
        EventQueue(
            storage: storage,
            sender: spy.sender,
            clock: clock,
            random: FakeRandom(value: random),
            options: EventQueueOptions(maxSize: maxSize, flushDelay: 3, baseBackoff: 1, maxBackoff: 300))
    }

    func testCapDropsTheOldestEvents() {
        let spy = SenderSpy()
        let queue = makeQueue(spy: spy, clock: FakeClock(), maxSize: 3)

        for index in 1...5 {
            queue.enqueue(.custom(name: "e\(index)"))
        }

        XCTAssertEqual(queue.count, 3)
        XCTAssertEqual(queue.events.map(\.name), ["e3", "e4", "e5"])
    }

    func testEnqueueStampsTheClockTimeWhenTheEventHasNone() {
        let clock = FakeClock()
        let queue = makeQueue(spy: SenderSpy(), clock: clock)
        let explicit = TestDates.moment.addingTimeInterval(-60)

        queue.enqueue(.custom(name: "unstamped"))
        queue.enqueue(.custom(name: "stamped", at: explicit))

        XCTAssertEqual(queue.events[0].timestamp, clock.now)
        XCTAssertEqual(queue.events[1].timestamp, explicit)
    }

    func testFlushSendsBatchesOfAtMostOneHundred() async {
        let spy = SenderSpy()
        let queue = makeQueue(spy: spy, clock: FakeClock())
        for index in 0..<150 {
            queue.enqueue(.custom(name: "e\(index)"))
        }

        let done = await queue.flush()

        XCTAssertTrue(done)
        XCTAssertEqual(queue.count, 0)
        XCTAssertEqual(spy.batches.current.map(\.count), [100, 50])
        XCTAssertEqual(spy.batches.current[0].first?.name, "e0", "oldest first")
        XCTAssertEqual(spy.batches.current[1].last?.name, "e149")
        XCTAssertEqual(DleAPI.maxEventsPerBatch, 100)
    }

    func testRetriableFailureKeepsTheEventsAndBacksOff() async {
        let clock = FakeClock()
        let spy = SenderSpy(failures: [.network("URLError(-1009)")])
        let queue = makeQueue(spy: spy, clock: clock, random: 0.5)
        queue.enqueue(.session())
        queue.enqueue(.custom(name: "a"))
        queue.enqueue(.custom(name: "b"))

        let first = await queue.flush()
        XCTAssertFalse(first)
        XCTAssertEqual(queue.count, 3, "a retriable failure keeps the batch")
        XCTAssertEqual(spy.batches.current.count, 1)

        // attempt 1, base 1 s, jitter 0.5 ⇒ 0.75 s. Before that nothing is sent.
        let tooEarly = await queue.flush()
        XCTAssertFalse(tooEarly)
        XCTAssertEqual(spy.batches.current.count, 1, "no send before the back-off elapses")

        clock.advance(by: 1)
        let retried = await queue.flush()
        XCTAssertTrue(retried)
        XCTAssertEqual(spy.batches.current.count, 2)
        XCTAssertEqual(spy.batches.current[1].count, 3)
        XCTAssertEqual(queue.count, 0)
    }

    func testRetryAfterFromTheServerIsHonoured() async {
        let clock = FakeClock()
        let spy = SenderSpy(failures: [.http(status: 429, problem: nil, retryAfter: 60)])
        let queue = makeQueue(spy: spy, clock: clock)
        queue.enqueue(.custom(name: "a"))

        XCTAssertFalse(await queue.flush())
        XCTAssertEqual(spy.batches.current.count, 1)

        clock.advance(by: 59)
        XCTAssertFalse(await queue.flush())
        XCTAssertEqual(spy.batches.current.count, 1, "Retry-After of 60 s means nothing at 59 s")

        clock.advance(by: 2)
        XCTAssertTrue(await queue.flush())
        XCTAssertEqual(spy.batches.current.count, 2)
    }

    func testNonRetriableFailureDropsTheBatch() async {
        let spy = SenderSpy(failures: [.http(status: 400, problem: DleProblem(type: DleProblem.validationFailed, status: 400), retryAfter: nil)])
        let queue = makeQueue(spy: spy, clock: FakeClock())
        queue.enqueue(.custom(name: "refused"))

        let done = await queue.flush()

        XCTAssertTrue(done, "a body the engine refuses today it refuses tomorrow")
        XCTAssertEqual(queue.count, 0)
        XCTAssertEqual(spy.batches.current.count, 1)
    }

    func testBackoffIsExponentialWithEqualJitterAndCapped() {
        XCTAssertEqual(EventQueue.backoffDelay(attempt: 1, base: 1, cap: 300, unitRandom: 0), 0.5)
        XCTAssertEqual(EventQueue.backoffDelay(attempt: 1, base: 1, cap: 300, unitRandom: 1), 1)
        XCTAssertEqual(EventQueue.backoffDelay(attempt: 5, base: 1, cap: 300, unitRandom: 0), 8)
        XCTAssertEqual(EventQueue.backoffDelay(attempt: 5, base: 1, cap: 300, unitRandom: 1), 16)
        XCTAssertEqual(EventQueue.backoffDelay(attempt: 20, base: 1, cap: 300, unitRandom: 0), 150)
        XCTAssertEqual(EventQueue.backoffDelay(attempt: 20, base: 1, cap: 300, unitRandom: 1), 300)
        XCTAssertEqual(EventQueue.backoffDelay(attempt: 0, base: 1, cap: 300, unitRandom: 0.5), 0.75, "attempt 0 behaves as attempt 1")
        XCTAssertEqual(EventQueue.backoffDelay(attempt: 3, base: 1, cap: 300, unitRandom: .nan), 2, "non-finite jitter is treated as 0")
    }

    func testStaleEventsAreDiscardedInsteadOfSent() async {
        let clock = FakeClock()
        let spy = SenderSpy()
        let queue = makeQueue(spy: spy, clock: clock)
        queue.enqueue(.custom(name: "old", at: clock.now.addingTimeInterval(-31 * 24 * 60 * 60)))
        queue.enqueue(.custom(name: "fresh"))

        let done = await queue.flush()

        XCTAssertTrue(done)
        XCTAssertEqual(spy.batches.current.map { $0.map(\.name) }, [["fresh"]])
    }

    func testQueuePersistsAcrossInstances() async throws {
        let storage = try TestSupport.makeStorage()
        let clock = FakeClock()
        let first = makeQueue(spy: SenderSpy(), clock: clock, storage: storage)
        first.enqueue(.custom(name: "a"))
        first.enqueue(.conversion(name: "purchase", value: 24.9, currency: "EUR"))
        first.shutdown()
        XCTAssertTrue(storage.exists(EventQueue.fileName))

        let spy = SenderSpy()
        let second = makeQueue(spy: spy, clock: clock, storage: storage)
        XCTAssertEqual(second.count, 2)
        XCTAssertEqual(second.events.map(\.name), ["a", "purchase"])
        XCTAssertEqual(second.events[1].value, 24.9)

        XCTAssertTrue(await second.flush())
        XCTAssertEqual(spy.batches.current.count, 1)
        XCTAssertFalse(storage.exists(EventQueue.fileName), "an empty queue leaves no file behind")
    }

    func testCorruptQueueFileIsDiscarded() throws {
        let storage = try TestSupport.makeStorage()
        try storage.write(Data("not json".utf8), to: EventQueue.fileName)

        let queue = makeQueue(spy: SenderSpy(), clock: FakeClock(), storage: storage)

        XCTAssertEqual(queue.count, 0)
        XCTAssertFalse(storage.exists(EventQueue.fileName))
    }

    func testShutdownRefusesFurtherEvents() {
        let queue = makeQueue(spy: SenderSpy(), clock: FakeClock())
        queue.enqueue(.custom(name: "before"))
        queue.shutdown()
        queue.enqueue(.custom(name: "after"))
        XCTAssertEqual(queue.events.map(\.name), ["before"])
    }

    func testRemoveAllDropsMatchingEventsOnly() {
        let queue = makeQueue(spy: SenderSpy(), clock: FakeClock())
        queue.enqueue(.custom(name: "behavioural"))
        queue.enqueue(.linkOpen(url: URL(string: "https://links.example.test/x")!))
        queue.removeAll { $0.type.isBehavioural }
        XCTAssertEqual(queue.events.map(\.type), [.linkOpen])
    }
}
