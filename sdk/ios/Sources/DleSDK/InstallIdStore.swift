import Foundation

#if canImport(Security)
import Security
#endif

/// Where the installation identifier is kept. The Keychain in production, memory in tests.
protocol KeychainStorage: Sendable {
    /// Reads the stored value, or `nil` when there is none.
    func read(service: String, account: String) throws -> String?
    /// Stores or replaces the value.
    func write(_ value: String, service: String, account: String) throws
    /// Removes the value; a missing one is not an error.
    func delete(service: String, account: String) throws
}

/// A Keychain failure, carrying the `OSStatus` for diagnostics.
struct KeychainError: Error, Sendable, Hashable {
    /// The Security framework status code.
    let status: Int32
}

#if canImport(Security)
/// The device Keychain: a generic-password item, `AfterFirstUnlockThisDeviceOnly`, never
/// synchronised to iCloud and never migrated to another device by a backup.
struct SystemKeychain: KeychainStorage {
    private func baseQuery(service: String, account: String) -> [CFString: Any] {
        [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: service,
            kSecAttrAccount: account,
            kSecAttrSynchronizable: false,
            kSecUseDataProtectionKeychain: true,
        ]
    }

    func read(service: String, account: String) throws -> String? {
        var query = baseQuery(service: service, account: account)
        query[kSecReturnData] = true
        query[kSecMatchLimit] = kSecMatchLimitOne
        var item: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &item)
        switch status {
        case errSecSuccess:
            guard let data = item as? Data else { return nil }
            return String(data: data, encoding: .utf8)
        case errSecItemNotFound:
            return nil
        default:
            throw KeychainError(status: status)
        }
    }

    func write(_ value: String, service: String, account: String) throws {
        let data = Data(value.utf8)
        var attributes = baseQuery(service: service, account: account)
        attributes[kSecValueData] = data
        attributes[kSecAttrAccessible] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        let status = SecItemAdd(attributes as CFDictionary, nil)
        if status == errSecDuplicateItem {
            let update: [CFString: Any] = [
                kSecValueData: data,
                kSecAttrAccessible: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
            ]
            let updateStatus = SecItemUpdate(baseQuery(service: service, account: account) as CFDictionary, update as CFDictionary)
            guard updateStatus == errSecSuccess else { throw KeychainError(status: updateStatus) }
            return
        }
        guard status == errSecSuccess else { throw KeychainError(status: status) }
    }

    func delete(service: String, account: String) throws {
        let status = SecItemDelete(baseQuery(service: service, account: account) as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else { throw KeychainError(status: status) }
    }
}
#endif

/// Tells an app update from a reinstall (see ``InstallIdStore``).
protocol InstallMarker: Sendable {
    /// `false` when the marker cannot be stored at all; the Keychain item is then trusted alone.
    var isAvailable: Bool { get }
    /// Whether the marker was present when the process started.
    func exists() -> Bool
    /// Creates the marker.
    func write()
}

/// The installation identifier: a random UUID v4 in the Keychain (FR-188, TC-143).
///
/// **Random, never derived.** It is the only identifier this SDK creates. It is not the
/// advertising identifier, not `identifierForVendor`, not a hash of anything about the device.
///
/// **Survives updates, not reinstalls.** Keychain items outlive the app that wrote them, which
/// is the property that makes them survive an update — and the property that would make a
/// reinstall look like the same installation. The app's own container does not outlive a
/// deletion, so a marker file there (the SDK's `state.json`) decides: Keychain item present and
/// marker present is an update; Keychain item present and marker gone is a reinstall, and the
/// identifier is rotated. A reinstalled app is a new user. That is the intended behaviour.
///
/// **`kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly`**: readable in the background after the
/// first unlock following a reboot, never included in a backup, never synchronised.
final class InstallIdStore: Sendable {
    /// Outcome of ``load()``.
    struct Loaded: Sendable, Hashable {
        /// The identifier to use for this process.
        let id: String
        /// `true` when the identifier was created just now (first launch, or reinstall).
        let isNew: Bool
        /// `false` when the identifier could not be written to the Keychain and will not
        /// survive the process. A resolve must not be attempted with such an id.
        let persisted: Bool
    }

    /// `kSecAttrService` of the item.
    static let service = "dev.dle.sdk.install-id"

    private let keychain: KeychainStorage
    private let marker: InstallMarker
    private let account: String

    /// Creates the store for one namespace.
    init(keychain: KeychainStorage, marker: InstallMarker, account: String) {
        self.keychain = keychain
        self.marker = marker
        self.account = account
    }

    /// Reads, rotates or creates the identifier according to the rules above.
    func load() -> Loaded {
        let existing: String?
        do {
            existing = try keychain.read(service: Self.service, account: account).flatMap { Self.isValid($0) ? $0 : nil }
        } catch {
            let status = (error as? KeychainError)?.status ?? -1
            DleLog.warning("keychain read failed (\(status)); using an ephemeral install id for this launch")
            return Loaded(id: Self.generate(), isNew: false, persisted: false)
        }

        if let existing {
            if marker.exists() || !marker.isAvailable {
                return Loaded(id: existing, isNew: false, persisted: true)
            }
            DleLog.info("install marker missing: reinstall detected, rotating the install id")
            try? keychain.delete(service: Self.service, account: account)
        }

        let fresh = Self.generate()
        var persisted = true
        do {
            try keychain.write(fresh, service: Self.service, account: account)
        } catch {
            let status = (error as? KeychainError)?.status ?? -1
            DleLog.warning("keychain write failed (\(status)); the install id will not survive this launch")
            persisted = false
        }
        if persisted {
            marker.write()
        }
        DleLog.debug("install id \(persisted ? "created" : "ephemeral"): \(DleLog.redact(identifier: fresh))")
        return Loaded(id: fresh, isNew: true, persisted: persisted)
    }

    /// A lowercase random UUID v4, the form the contract examples use.
    static func generate() -> String {
        UUID().uuidString.lowercased()
    }

    /// `true` for a lowercase 8-4-4-4-12 hexadecimal UUID.
    static func isValid(_ id: String) -> Bool {
        let parts = id.split(separator: "-", omittingEmptySubsequences: false)
        let lengths = [8, 4, 4, 4, 12]
        guard parts.count == lengths.count else { return false }
        for (part, length) in zip(parts, lengths) {
            guard part.count == length, part.allSatisfy({ $0.isHexDigit && !$0.isUppercase }) else { return false }
        }
        return true
    }
}
