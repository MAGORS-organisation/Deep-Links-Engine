import Foundation

/// Minimal mutual-exclusion box used for the very small amount of state that has to be
/// reachable from synchronous, non-isolated entry points (SDK bootstrap, log level,
/// notification callbacks). Everything else in this SDK lives inside an actor.
///
/// `@unchecked Sendable` is justified: `value` is only ever touched while `lock` is held.
final class Locked<Value>: @unchecked Sendable {
    private let lock = NSLock()
    private var value: Value

    init(_ value: Value) {
        self.value = value
    }

    /// Runs `body` with exclusive access to the boxed value.
    @discardableResult
    func withLock<R>(_ body: (inout Value) throws -> R) rethrows -> R {
        lock.lock()
        defer { lock.unlock() }
        return try body(&value)
    }

    /// Snapshot of the current value.
    var current: Value {
        withLock { $0 }
    }
}
