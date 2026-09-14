import Foundation

/// The little that must survive relaunches and app updates: whether this installation has
/// already called `POST /v1/resolve`, and what the engine answered (TC-143).
struct PersistedState: Codable, Sendable, Equatable {
    /// Layout version of the file.
    var schema: Int = 1
    /// The engine's once-per-installation answer, when it has been obtained.
    var deferredLink: DeferredLink?
    /// When ``deferredLink`` was obtained.
    var resolvedAt: Date?
    /// Whether the `first_open` event has been queued for this installation.
    var firstOpenTracked: Bool = false

    private enum CodingKeys: String, CodingKey {
        case schema
        case deferredLink = "deferred_link"
        case resolvedAt = "resolved_at"
        case firstOpenTracked = "first_open_tracked"
    }

    /// An empty state.
    init() {}

    /// Decodes leniently; a corrupt member is treated as absent.
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        schema = (try? c.decodeIfPresent(Int.self, forKey: .schema)) ?? nil ?? 1
        deferredLink = (try? c.decodeIfPresent(DeferredLink.self, forKey: .deferredLink)) ?? nil
        if let raw = (try? c.decodeIfPresent(String.self, forKey: .resolvedAt)) ?? nil {
            resolvedAt = DleWireDate.date(from: raw)
        }
        firstOpenTracked = (try? c.decodeIfPresent(Bool.self, forKey: .firstOpenTracked)) ?? nil ?? false
    }

    func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(schema, forKey: .schema)
        try c.encodeIfPresent(deferredLink, forKey: .deferredLink)
        if let resolvedAt {
            try c.encode(DleWireDate.string(from: resolvedAt), forKey: .resolvedAt)
        }
        try c.encode(firstOpenTracked, forKey: .firstOpenTracked)
    }
}

/// Owner of ``PersistedState``: in memory behind a lock, mirrored to `state.json`.
///
/// The presence of the file doubles as the "this app has run before" marker that
/// ``InstallIdStore`` uses to tell an update (file present, Keychain item present) from a
/// reinstall (file gone, Keychain item still there).
final class StateStore: Sendable {
    /// File name inside the storage directory.
    static let fileName = "state.json"

    private let storage: DleStorage?
    private let state: Locked<PersistedState>
    private let existedAtLaunch: Bool

    /// Loads the state from `storage`, or starts empty.
    init(storage: DleStorage?) {
        self.storage = storage
        let existed = storage?.exists(Self.fileName) ?? false
        self.existedAtLaunch = existed
        var loaded = PersistedState()
        if existed, let data = storage?.read(Self.fileName),
           let decoded = try? DleAPI.makeDecoder().decode(PersistedState.self, from: data)
        {
            loaded = decoded
        }
        self.state = Locked(loaded)
    }

    /// Snapshot of the current state.
    var current: PersistedState {
        state.current
    }

    /// The persisted resolve result, if any.
    var deferredLink: DeferredLink? {
        state.current.deferredLink
    }

    /// Mutates the state and writes it through.
    func update(_ body: (inout PersistedState) -> Void) {
        state.withLock { s in
            body(&s)
            save(s)
        }
    }

    /// Replaces the state with an empty one and writes it through, which also creates the
    /// install marker.
    func reset() {
        state.withLock { s in
            s = PersistedState()
            save(s)
        }
    }

    private func save(_ value: PersistedState) {
        guard let storage else { return }
        do {
            try storage.write(try DleAPI.makeEncoder().encode(value), to: Self.fileName)
        } catch {
            DleLog.warning("could not persist SDK state (\(Swift.type(of: error)))")
        }
    }
}

extension StateStore: InstallMarker {
    var isAvailable: Bool {
        storage != nil
    }

    func exists() -> Bool {
        existedAtLaunch
    }

    func write() {
        reset()
    }
}
