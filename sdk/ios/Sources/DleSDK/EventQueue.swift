import Foundation

/// Sends one batch. Throws ``DleError``; ``DleError/isRetriable`` decides whether the batch is
/// kept for another attempt or dropped.
typealias EventSender = @Sendable ([DleEvent]) async throws -> Void

/// Tunables of ``EventQueue``.
struct EventQueueOptions: Sendable {
    /// Upper bound of queued events; the oldest are dropped beyond it.
    var maxSize = 200
    /// Debounce before an automatic flush.
    var flushDelay: TimeInterval = 3
    /// Events older than this are discarded (`RecordEvents.PastTolerance` is 30 days).
    var maxAge: TimeInterval = 30 * 24 * 60 * 60
    /// First back-off step.
    var baseBackoff: TimeInterval = 1
    /// Longest back-off step.
    var maxBackoff: TimeInterval = 300
    /// Largest batch (`EventBatchDto.MaxEventsPerBatch`).
    var maxBatch = DleAPI.maxEventsPerBatch
}

/// One queued event plus the identity the queue needs to remove exactly it after a send.
struct QueuedEvent: Codable, Sendable, Hashable {
    /// Random per-entry identifier. Never leaves the device.
    let id: String
    /// The event.
    let event: DleEvent
}

/// Offline event queue with persistence and retry (FR-226, spec §C.5).
///
/// * `enqueue` is synchronous and safe from any thread, so a universal-link callback that has
///   to return a `Bool` to UIKit can report a `link_open` without leaving the call stack.
/// * Every mutation is written through to `queue.json` atomically, so the queue survives being
///   killed and offline spells.
/// * Flushes are debounced and batched, at most 100 events per request, so a burst costs one
///   request rather than one each.
/// * Retriable failures — network, timeout, 408, 429, 5xx — keep the events and back off
///   exponentially with jitter, honouring `Retry-After`. Non-retriable failures (400, 401, 403,
///   413) drop the batch: a body the engine refuses today it will refuse tomorrow.
/// * The queue holds events only, never an identifier; `install_id` is attached at send time.
///
/// Concurrency: state lives behind a lock; the only `Task` the queue ever creates is one
/// worker at a time, stored in that state and cancelled by ``shutdown()``. Concurrent flushes
/// are coalesced by ``FlushCoordinator`` rather than run in parallel.
final class EventQueue: Sendable {
    /// File name inside the storage directory.
    static let fileName = "queue.json"

    private struct State {
        var items: [QueuedEvent]
        var attempt = 0
        var nextAttemptAt: Date?
        var worker: Task<Void, Never>?
        var isShutDown = false
    }

    private let state: Locked<State>
    private let storage: DleStorage?
    private let sender: EventSender
    private let clock: DleClock
    private let random: DleRandomSource
    private let options: EventQueueOptions
    private let flushes = FlushCoordinator()

    /// Creates the queue, loading whatever an earlier process left on disk.
    init(
        storage: DleStorage?,
        sender: @escaping EventSender,
        clock: DleClock,
        random: DleRandomSource,
        options: EventQueueOptions
    ) {
        self.storage = storage
        self.sender = sender
        self.clock = clock
        self.random = random
        self.options = options
        let loaded = Self.load(from: storage, now: clock.now, options: options)
        self.state = Locked(State(items: loaded))
        if !loaded.isEmpty {
            schedule(after: options.flushDelay)
        }
    }

    /// Number of queued events.
    var count: Int {
        state.current.items.count
    }

    /// Snapshot of the queued events, oldest first.
    var events: [DleEvent] {
        state.current.items.map(\.event)
    }

    /// Appends an event, stamping it with the current time when it has no timestamp, and
    /// schedules a debounced flush.
    func enqueue(_ event: DleEvent) {
        var stamped = event
        if stamped.timestamp == nil {
            stamped.timestamp = clock.now
        }
        let entry = QueuedEvent(id: UUID().uuidString, event: stamped)
        var dropped = 0
        state.withLock { (s: inout State) in
            guard !s.isShutDown else { return }
            s.items.append(entry)
            if s.items.count > options.maxSize {
                dropped = s.items.count - options.maxSize
                s.items.removeFirst(dropped)
            }
            persist(s.items)
        }
        if dropped > 0 {
            DleLog.warning("event queue full (\(options.maxSize)); dropped \(dropped) oldest event(s)")
        }
        schedule(after: options.flushDelay)
    }

    /// Removes every queued event matching `predicate`.
    func removeAll(where predicate: (DleEvent) -> Bool) {
        state.withLock { (s: inout State) in
            let before = s.items.count
            s.items.removeAll { predicate($0.event) }
            if s.items.count != before {
                persist(s.items)
            }
        }
    }

    /// Schedules an immediate flush without waiting for it.
    func requestFlush() {
        schedule(after: 0)
    }

    /// Sends what is queued. Returns `true` when the queue is empty afterwards.
    @discardableResult
    func flush() async -> Bool {
        let done = await flushes.run { [self] in await self.drain() }
        if !done {
            schedule(after: 0)
        }
        return done
    }

    /// Cancels the worker and refuses further events. An in-flight request completes.
    func shutdown() {
        let worker: Task<Void, Never>? = state.withLock { (s: inout State) in
            s.isShutDown = true
            let current = s.worker
            s.worker = nil
            return current
        }
        worker?.cancel()
    }

    /// Exponential back-off with equal jitter: half of `base · 2^(attempt−1)` (capped at `cap`)
    /// plus a random share of the other half, so a fleet of devices does not retry in lockstep.
    static func backoffDelay(attempt: Int, base: TimeInterval, cap: TimeInterval, unitRandom: Double) -> TimeInterval {
        let exponent = max(0, min(attempt - 1, 30))
        let raw = min(cap, base * pow(2, Double(exponent)))
        let jitter = min(1, max(0, unitRandom.isFinite ? unitRandom : 0))
        return raw / 2 + raw / 2 * jitter
    }

    // MARK: - Worker

    private func schedule(after delay: TimeInterval) {
        state.withLock { (s: inout State) in
            guard !s.isShutDown, s.worker == nil, !s.items.isEmpty else { return }
            s.worker = Task { [self] in await self.runWorker(initialDelay: delay) }
        }
    }

    private func clearWorker() {
        state.withLock { (s: inout State) in
            s.worker = nil
        }
    }

    private func runWorker(initialDelay: TimeInterval) async {
        let wait: TimeInterval = state.withLock { (s: inout State) in
            let remaining = s.nextAttemptAt.map { $0.timeIntervalSince(clock.now) } ?? 0
            return max(initialDelay, remaining, 0)
        }
        do {
            try await clock.sleep(seconds: wait)
        } catch {
            clearWorker()
            return
        }
        guard !Task.isCancelled else {
            clearWorker()
            return
        }
        let done = await flushes.run { [self] in await self.drain() }
        clearWorker()
        if !done, !Task.isCancelled {
            schedule(after: 0)
        }
    }

    /// Sends batches until the queue is empty, a back-off starts, or the task is cancelled.
    private func drain() async -> Bool {
        while true {
            if Task.isCancelled { return false }
            let now = clock.now
            let batch: [QueuedEvent] = state.withLock { (s: inout State) in
                if let at = s.nextAttemptAt, at > now { return [] }
                let cutoff = now.addingTimeInterval(-options.maxAge)
                let before = s.items.count
                s.items.removeAll { ($0.event.timestamp ?? now) < cutoff }
                if s.items.count != before {
                    DleLog.debug("discarded \(before - s.items.count) event(s) older than the engine accepts")
                    persist(s.items)
                }
                return Array(s.items.prefix(options.maxBatch))
            }
            if batch.isEmpty {
                return state.current.items.isEmpty
            }
            do {
                try await sender(batch.map(\.event))
                remove(batch)
                state.withLock { (s: inout State) in
                    s.attempt = 0
                    s.nextAttemptAt = nil
                }
                DleLog.debug("flushed \(batch.count) event(s)")
            } catch is CancellationError {
                return false
            } catch let error as DleError where !error.isRetriable {
                DleLog.warning("dropping \(batch.count) event(s): \(error.localizedDescription)")
                remove(batch)
            } catch {
                let retryAfter = (error as? DleError)?.retryAfter
                let delay: TimeInterval = state.withLock { (s: inout State) in
                    s.attempt += 1
                    let computed = retryAfter ?? Self.backoffDelay(
                        attempt: s.attempt, base: options.baseBackoff, cap: options.maxBackoff, unitRandom: random.unitRandom())
                    s.nextAttemptAt = clock.now.addingTimeInterval(computed)
                    return computed
                }
                DleLog.debug("flush failed (\(error.localizedDescription)); retry in \(Int(delay.rounded())) s")
                return false
            }
        }
    }

    private func remove(_ batch: [QueuedEvent]) {
        let ids = Set(batch.map(\.id))
        state.withLock { (s: inout State) in
            s.items.removeAll { ids.contains($0.id) }
            persist(s.items)
        }
    }

    // MARK: - Persistence

    private func persist(_ items: [QueuedEvent]) {
        guard let storage else { return }
        if items.isEmpty {
            storage.remove(Self.fileName)
            return
        }
        do {
            try storage.write(try DleAPI.makeEncoder().encode(items), to: Self.fileName)
        } catch {
            DleLog.warning("could not persist the event queue (\(Swift.type(of: error)))")
        }
    }

    private static func load(from storage: DleStorage?, now: Date, options: EventQueueOptions) -> [QueuedEvent] {
        guard let storage, let data = storage.read(fileName) else { return [] }
        guard let decoded = try? DleAPI.makeDecoder().decode([FailableDecodable<QueuedEvent>].self, from: data) else {
            storage.remove(fileName)
            return []
        }
        let cutoff = now.addingTimeInterval(-options.maxAge)
        let items = decoded.compactMap(\.value).filter { ($0.event.timestamp ?? now) >= cutoff }
        return Array(items.suffix(options.maxSize))
    }
}

/// Serialises flushes: a flush requested while one is running joins it instead of racing it.
actor FlushCoordinator {
    private var current: Task<Bool, Never>?

    /// Runs `body`, or returns the result of the flush already in progress.
    func run(_ body: @escaping @Sendable () async -> Bool) async -> Bool {
        if let current {
            return await current.value
        }
        let task = Task { await body() }
        current = task
        let result = await task.value
        current = nil
        return result
    }
}
