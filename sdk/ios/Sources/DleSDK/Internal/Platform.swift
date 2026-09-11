import Foundation

/// What this build reports as `platform` and `os_version`.
enum DlePlatform {
    /// Wire value of `ResolveRequestDto.platform`: `ios`, `desktop` or `other`
    /// (`Dle.Domain.Clients.Platform`). Mac Catalyst is a desktop; visionOS runs the iOS
    /// ecosystem and reports as `ios`.
    static let wireName: String = {
        #if targetEnvironment(macCatalyst)
        return "desktop"
        #elseif os(iOS) || os(visionOS)
        return "ios"
        #elseif os(macOS)
        return "desktop"
        #else
        return "other"
        #endif
    }()

    /// Operating system version as `major.minor` or `major.minor.patch`, for example `17.5`
    /// or `18.0.1`. `ProcessInfo.operatingSystemVersion` is not a required-reason API.
    static func osVersion() -> String {
        let v = ProcessInfo.processInfo.operatingSystemVersion
        if v.patchVersion == 0 {
            return "\(v.majorVersion).\(v.minorVersion)"
        }
        return "\(v.majorVersion).\(v.minorVersion).\(v.patchVersion)"
    }
}
