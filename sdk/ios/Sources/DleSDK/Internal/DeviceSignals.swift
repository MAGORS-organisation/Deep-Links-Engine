import Foundation

#if canImport(UIKit)
import UIKit
#elseif canImport(AppKit)
import AppKit
#endif

/// Collects the coarse device signals of `DeviceSignalsDto` — and only when asked.
///
/// Four values, each shared by millions of devices: the UI language, the physical screen size,
/// the UTC offset and the hardware model string. Nothing is hashed, nothing is combined, and the
/// caller invokes this only when the operator opted in to probabilistic matching AND the user
/// granted attribution consent (spec §0.2, §E.6.2, TC-146). The identifier-bearing APIs —
/// `ASIdentifierManager`, `identifierForVendor`, `UIPasteboard` — are not referenced anywhere in
/// this package, and none of the calls below is a required-reason API.
enum DeviceSignalsCollector {
    /// Reads the signals. Main-actor bound because the screen is.
    @MainActor
    static func collect() -> DeviceSignalsBody {
        DeviceSignalsBody(
            language: language(),
            screen: screen(),
            tzOffset: TimeZone.current.secondsFromGMT() / 60,
            deviceModel: model())
    }

    /// BCP 47 tag of the preferred UI language, for example `sk-SK`.
    static func language() -> String? {
        if let first = Locale.preferredLanguages.first, !first.isEmpty {
            return first
        }
        let identifier = Locale.current.identifier.replacingOccurrences(of: "_", with: "-")
        return identifier.isEmpty ? nil : identifier
    }

    /// Physical pixels, portrait-oriented, as `WIDTHxHEIGHT`, for example `1179x2556`.
    @MainActor
    static func screen() -> String? {
        #if os(iOS) || os(tvOS)
        let bounds = UIScreen.main.nativeBounds
        return "\(Int(bounds.width.rounded()))x\(Int(bounds.height.rounded()))"
        #elseif os(macOS)
        guard let screen = NSScreen.main else { return nil }
        let scale = screen.backingScaleFactor
        let width = Int((screen.frame.width * scale).rounded())
        let height = Int((screen.frame.height * scale).rounded())
        return "\(width)x\(height)"
        #else
        return nil
        #endif
    }

    /// The hardware model string, for example `iPhone15,2`. Coarse: every unit of one model
    /// reports the same value.
    static func model() -> String? {
        var system = utsname()
        guard uname(&system) == 0 else { return nil }
        let capacity = MemoryLayout.size(ofValue: system.machine)
        let machine = withUnsafePointer(to: &system.machine) { pointer in
            pointer.withMemoryRebound(to: CChar.self, capacity: capacity) { String(cString: $0) }
        }
        return machine.isEmpty ? nil : machine
    }
}
