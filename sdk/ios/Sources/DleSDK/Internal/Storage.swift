import Foundation

/// The SDK's on-device files: `<Application Support>/DleSDK/<namespace>/`.
///
/// Files rather than `UserDefaults`, on purpose. `UserDefaults` is a required-reason API
/// (`NSPrivacyAccessedAPICategoryUserDefaults`), and touching it here would oblige every
/// integrator to carry the declaration. Plain file reads and writes are not on Apple's list,
/// and the SDK never reads file timestamps (`NSPrivacyAccessedAPICategoryFileTimestamp`), so the
/// package's privacy manifest declares no accessed API at all (spec §E.7 item 4, §C.5).
///
/// The directory is excluded from backups: its contents describe one installation on one device,
/// and restoring them onto another device would only produce an installation the engine has
/// never seen.
struct DleStorage: Sendable {
    /// Directory every file of this namespace lives in.
    let directory: URL

    /// The production location for a namespace, or `nil` when the container is unavailable —
    /// the SDK then keeps its state in memory for the lifetime of the process.
    static func standard(namespace: String) -> DleStorage? {
        guard let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first else {
            DleLog.warning("no Application Support directory; SDK state will not persist")
            return nil
        }
        let root = base.appendingPathComponent("DleSDK", isDirectory: true)
        let storage = DleStorage(directory: root.appendingPathComponent(namespace, isDirectory: true))
        do {
            try storage.prepare(root: root)
        } catch {
            DleLog.warning("could not create the SDK state directory; state will not persist")
            return nil
        }
        return storage
    }

    /// Creates the directory tree and marks it as not to be backed up.
    private func prepare(root: URL) throws {
        var attributes: [FileAttributeKey: Any] = [:]
        #if os(iOS) || os(tvOS) || os(watchOS) || os(visionOS)
        // Readable after the first unlock following a reboot, so a background flush can read
        // the queue; never readable while the device has not been unlocked at all.
        attributes[.protectionKey] = FileProtectionType.completeUntilFirstUserAuthentication
        #endif
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: attributes)

        var values = URLResourceValues()
        values.isExcludedFromBackup = true
        var mutableRoot = root
        try? mutableRoot.setResourceValues(values)
    }

    /// Reads a file, or `nil` when it does not exist or cannot be read.
    func read(_ name: String) -> Data? {
        try? Data(contentsOf: directory.appendingPathComponent(name, isDirectory: false))
    }

    /// Writes a file atomically.
    func write(_ data: Data, to name: String) throws {
        var options: Data.WritingOptions = [.atomic]
        #if os(iOS) || os(tvOS) || os(watchOS) || os(visionOS)
        options.insert(.completeFileProtectionUntilFirstUserAuthentication)
        #endif
        try data.write(to: directory.appendingPathComponent(name, isDirectory: false), options: options)
    }

    /// Whether a file exists.
    func exists(_ name: String) -> Bool {
        FileManager.default.fileExists(atPath: directory.appendingPathComponent(name, isDirectory: false).path)
    }

    /// Deletes a file, ignoring a missing one.
    func remove(_ name: String) {
        try? FileManager.default.removeItem(at: directory.appendingPathComponent(name, isDirectory: false))
    }
}

/// Decodes one element of an array, yielding `nil` instead of failing the whole array.
struct FailableDecodable<Value: Decodable>: Decodable {
    /// The element, or `nil` when it could not be decoded.
    let value: Value?

    init(from decoder: Decoder) throws {
        value = try? Value(from: decoder)
    }
}
