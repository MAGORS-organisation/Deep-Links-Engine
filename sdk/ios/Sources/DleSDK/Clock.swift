import Foundation

/// Abstraction over "what time is it" and "wait a bit".
///
/// The SDK never calls `Date()` or `Task.sleep` directly; everything goes through a clock so
/// that retry/backoff behaviour is deterministically testable (mirrors the server-side rule of
/// using `TimeProvider` instead of `DateTime.UtcNow`, SHARED-KERNEL §17.2).
public protocol DleClock: Sendable {
    /// The current wall-clock instant.
    var now: Date { get }

    /// Suspends the current task for `seconds`. A non-positive value returns immediately.
    /// - Throws: `CancellationError` if the surrounding task is cancelled while waiting.
    func sleep(seconds: TimeInterval) async throws
}

/// The production clock: system time and `Task.sleep`.
public struct DleSystemClock: DleClock {
    /// Longest single sleep the SDK will ever perform (one hour). Guards against overflow
    /// when a misconfigured backoff produces an absurd delay.
    public static let maximumSleep: TimeInterval = 3600

    /// Creates a system clock.
    public init() {}

    /// The current wall-clock instant.
    public var now: Date { Date() }

    /// Suspends the current task for `seconds`, clamped to `maximumSleep`.
    public func sleep(seconds: TimeInterval) async throws {
        guard seconds > 0 else { return }
        let clamped = min(seconds, Self.maximumSleep)
        try await Task.sleep(nanoseconds: UInt64((clamped * 1_000_000_000).rounded()))
    }
}

/// Source of randomness used for backoff jitter. Injectable so tests are deterministic.
public protocol DleRandomSource: Sendable {
    /// Returns a value in `0.0 ... 1.0`.
    func unitRandom() -> Double
}

/// Production randomness, backed by the system RNG.
public struct DleSystemRandomSource: DleRandomSource {
    /// Creates a system random source.
    public init() {}

    /// Returns a value in `0.0 ... 1.0`.
    public func unitRandom() -> Double {
        Double.random(in: 0...1)
    }
}
