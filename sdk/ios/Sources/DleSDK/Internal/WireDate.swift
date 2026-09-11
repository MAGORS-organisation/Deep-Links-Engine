import Foundation

/// Renders and parses the ISO 8601 instants of the wire contract.
///
/// The engine serialises `DateTimeOffset` as `2026-09-03T10:00:00+00:00` — an explicit zero
/// offset rather than `Z`, and fractional seconds only when they are non-zero. The SDK writes
/// exactly that form, so a body it produces is byte-comparable with the literals of
/// `tests/Dle.ContractTests/Wire/SdkWireContractTests.cs`, and it reads both forms, because the
/// specification's example body uses `Z`.
enum DleWireDate {
    /// Renders an instant in UTC, millisecond precision, `+00:00` suffix.
    static func string(from date: Date) -> String {
        let millis = (date.timeIntervalSince1970 * 1000).rounded()
        let hasFraction = millis.truncatingRemainder(dividingBy: 1000) != 0
        let formatter = ISO8601DateFormatter()
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        formatter.formatOptions = hasFraction
            ? [.withInternetDateTime, .withFractionalSeconds]
            : [.withInternetDateTime]
        var rendered = formatter.string(from: Date(timeIntervalSince1970: millis / 1000))
        if rendered.hasSuffix("Z") {
            rendered.removeLast()
            rendered += "+00:00"
        }
        return rendered
    }

    /// Parses `Z` or `±hh:mm` offsets, with or without fractional seconds (any number of digits;
    /// the engine writes up to seven).
    static func date(from string: String) -> Date? {
        let trimmed = string.trimmingCharacters(in: .whitespacesAndNewlines)
        let formatter = ISO8601DateFormatter()
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = formatter.date(from: normalizeFraction(trimmed, keep: true)) {
            return date
        }
        formatter.formatOptions = [.withInternetDateTime]
        return formatter.date(from: normalizeFraction(trimmed, keep: false))
    }

    /// Rewrites the fractional-seconds part to exactly three digits, or removes it.
    private static func normalizeFraction(_ value: String, keep: Bool) -> String {
        guard let timeStart = value.firstIndex(of: "T"),
              let dot = value[timeStart...].firstIndex(of: ".")
        else {
            return value
        }
        let afterDot = value[value.index(after: dot)...]
        let digits = afterDot.prefix { $0.isNumber }
        let rest = afterDot.dropFirst(digits.count)
        guard !digits.isEmpty else { return value }
        if !keep {
            return String(value[..<dot]) + String(rest)
        }
        var fraction = String(digits.prefix(3))
        while fraction.count < 3 { fraction += "0" }
        return String(value[..<dot]) + "." + fraction + String(rest)
    }
}
