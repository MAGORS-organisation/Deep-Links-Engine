import Foundation

#if canImport(os)
import os
#endif

/// Verbosity of the SDK's diagnostic output.
public enum DleLogLevel: Int, Sendable, Comparable, CaseIterable, Codable {
    /// No output at all.
    case off = 0
    /// Failures only.
    case error = 1
    /// Failures and recoverable problems. Default.
    case warning = 2
    /// Lifecycle milestones (configured, resolved, flushed).
    case info = 3
    /// Everything, including per-request outcomes. Never enable in a shipping build.
    case debug = 4

    /// Orders levels by verbosity.
    public static func < (lhs: DleLogLevel, rhs: DleLogLevel) -> Bool {
        lhs.rawValue < rhs.rawValue
    }
}

/// The SDK's logger.
///
/// Hard rules, enforced by review and by `DleLog.redact(_:)`:
/// * never log the SDK key or any `Authorization` value,
/// * never log a full URL including its query string,
/// * never log the raw `install_id` — only its short prefix.
///
/// These mirror the server-side logging prohibitions (spec S-08, T-08, SHARED-KERNEL §17.5).
public enum DleLog {
    /// A destination for log lines. Supply your own to bridge into the host app's logging stack.
    public typealias Sink = @Sendable (DleLogLevel, String) -> Void

    private struct State {
        var level: DleLogLevel = .warning
        var sink: Sink?
    }

    private static let state = Locked(State())

    /// The current verbosity. Defaults to `.warning`.
    public static var level: DleLogLevel {
        get { state.withLock { $0.level } }
        set { state.withLock { $0.level = newValue } }
    }

    /// Installs a custom sink. Pass `nil` to restore the default (unified logging / `print`).
    public static func setSink(_ sink: Sink?) {
        state.withLock { $0.sink = sink }
    }

    /// Logs a failure.
    public static func error(_ message: @autoclosure () -> String) {
        emit(.error, message())
    }

    /// Logs a recoverable problem.
    public static func warning(_ message: @autoclosure () -> String) {
        emit(.warning, message())
    }

    /// Logs a lifecycle milestone.
    public static func info(_ message: @autoclosure () -> String) {
        emit(.info, message())
    }

    /// Logs a fine-grained detail.
    public static func debug(_ message: @autoclosure () -> String) {
        emit(.debug, message())
    }

    private static func emit(_ level: DleLogLevel, _ message: @autoclosure () -> String) {
        let snapshot = state.current
        guard snapshot.level != .off, level <= snapshot.level else { return }
        let line = message()
        if let sink = snapshot.sink {
            sink(level, line)
            return
        }
        #if canImport(os)
        let logger = Logger(subsystem: "dev.dle.sdk", category: "DleSDK")
        switch level {
        case .off: break
        case .error: logger.error("\(line, privacy: .public)")
        case .warning: logger.warning("\(line, privacy: .public)")
        case .info: logger.info("\(line, privacy: .public)")
        case .debug: logger.debug("\(line, privacy: .public)")
        }
        #else
        print("[DleSDK] \(line)")
        #endif
    }

    /// Returns a log-safe rendering of a URL: scheme, host and path only — never the query
    /// string or fragment, which routinely carry campaign parameters and can carry secrets.
    public static func redact(_ url: URL) -> String {
        var out = ""
        if let scheme = url.scheme { out += scheme + "://" }
        out += url.host ?? "?"
        out += url.path
        if url.query != nil { out += "?<redacted>" }
        return out
    }

    /// Returns a log-safe rendering of an opaque identifier: first 8 characters only.
    public static func redact(identifier: String) -> String {
        String(identifier.prefix(8)) + "…"
    }
}
