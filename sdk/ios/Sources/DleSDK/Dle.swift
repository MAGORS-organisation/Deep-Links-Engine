import Foundation

#if canImport(UIKit) && !os(watchOS)
import UIKit
#endif

/// The Deep Link Engine SDK.
///
/// ```swift
/// try Dle.configure(DleConfig(
///     endpoint: URL(string: "https://links.example.sk")!,
///     sdkKey: "dle_pk_…",
///     linkHosts: ["links.example.sk"]))
///
/// let link = try await Dle.shared.resolve()          // once per installation
/// Dle.shared.handle(url: url)                          // .onOpenURL → link_open
/// ```
///
/// What it does, and only this:
/// * **Resolve once.** The first call to ``resolve(claimCode:loginKey:)`` after an install
///   posts `POST /v1/resolve` with the installation id, `ios`, the app and OS versions and the
///   consent record; the answer is persisted and every later call returns it without a request
///   (TC-143). Device signals are attached only when ``DleDeferredStrategies/probabilistic`` is
///   enabled **and** ``DleConsent/attribution`` was granted; otherwise the `signals` member is
///   omitted from the body, not sent empty (TC-145, TC-146).
/// * **Report direct opens.** ``handle(_:)`` and ``handle(url:)`` queue a `link_open` for every
///   Universal Link the operating system hands the app, because a direct open reaches no
///   server and is otherwise invisible (FR-223).
/// * **Batch events.** ``track(_:)`` queues; the queue persists, batches at most 100 events per
///   request, retries with back-off, honours `Retry-After`, and flushes when the app enters the
///   foreground.
///
/// What it never does: read the pasteboard, touch `ASIdentifierManager` or App Tracking
/// Transparency, derive an identifier from the device, or put anything on a custom-scheme URL
/// (spec §C.5, §E.7). The only identifier it creates is a random UUID kept in the Keychain.
///
/// Thread-safety: every method is callable from any thread. The class is a `Sendable` final
/// class rather than an actor because ``handle(_:)`` and ``handle(url:)`` have to return
/// synchronously to UIKit's delegate callbacks; the small amount of mutable state lives behind
/// locks, exactly as in ``EventQueue``.
public final class Dle: Sendable {
    /// SDK version, reported in the `User-Agent` header.
    public static let version = "0.1.0"

    private static let registry = Locked<Dle?>(nil)

    // MARK: - Configuration

    /// Configures the SDK. Call once, early — from `App.init` or
    /// `application(_:didFinishLaunchingWithOptions:)` — and before anything else.
    ///
    /// Calling it again replaces the previous instance: its queue is shut down (an in-flight
    /// request completes) and its foreground observers are removed.
    ///
    /// - Throws: ``DleError/invalidConfiguration(_:)`` naming the first unusable field.
    /// - Returns: The configured instance, also available as ``shared``.
    @discardableResult
    public static func configure(_ config: DleConfig) throws -> Dle {
        // Validate before creating anything on disk; `init` validates again, cheaply.
        let validated = try config.validated()
        let storage = DleStorage.standard(namespace: validated.storageNamespace)
        #if canImport(Security)
        let keychain: KeychainStorage = SystemKeychain()
        #else
        let keychain: KeychainStorage = InMemoryKeychain()
        #endif
        let instance = try Dle(
            config: validated,
            transport: URLSessionTransport(timeout: validated.requestTimeout),
            keychain: keychain,
            storage: storage,
            clock: DleSystemClock(),
            random: DleSystemRandomSource())
        install(instance)
        return instance
    }

    /// Makes `instance` the shared one, retiring the previous one.
    static func install(_ instance: Dle) {
        let previous: Dle? = registry.withLock { current in
            defer { current = instance }
            return current
        }
        previous?.shutdown()
        DleLog.info("DleSDK \(version) configured (\(instance.config.deferredStrategies.names.joined(separator: ",")))")
    }

    /// The instance created by ``configure(_:)``.
    /// - Throws: ``DleError/notConfigured`` before ``configure(_:)`` has been called.
    public static var shared: Dle {
        get throws {
            guard let instance = registry.current else { throw DleError.notConfigured }
            return instance
        }
    }

    /// `true` once ``configure(_:)`` has succeeded.
    public static var isConfigured: Bool {
        registry.current != nil
    }

    // MARK: - State

    /// The validated configuration.
    public let config: DleConfig

    let api: DleAPI
    let stateStore: StateStore
    let queue: EventQueue
    let links: UniversalLinkHandler
    let clock: DleClock
    private let installIdLoaded: InstallIdStore.Loaded
    private let consentBox: Locked<DleConsent>
    private let resolves = ResolveSerializer()
    private let observers = Locked<[any NSObjectProtocol]>([])

    /// Creates an instance over explicit dependencies. ``configure(_:)`` supplies the
    /// production ones; tests supply fakes.
    init(
        config: DleConfig,
        transport: DleTransport,
        keychain: KeychainStorage,
        storage: DleStorage?,
        clock: DleClock,
        random: DleRandomSource
    ) throws {
        let validated = try config.validated()
        DleLog.level = validated.logLevel
        self.config = validated
        self.clock = clock

        let stateStore = StateStore(storage: storage)
        self.stateStore = stateStore
        let loaded = InstallIdStore(keychain: keychain, marker: stateStore, account: validated.storageNamespace).load()
        self.installIdLoaded = loaded

        let api = DleAPI(
            endpoint: validated.endpoint,
            sdkKey: validated.sdkKey,
            transport: transport,
            timeout: validated.requestTimeout,
            clock: clock)
        self.api = api
        self.consentBox = Locked(validated.consent)

        // The queue holds events only; the identity is attached here, at send time.
        let installId = loaded.id
        let platform = DlePlatform.wireName
        let appVersion = validated.appVersion
        let queue = EventQueue(
            storage: storage,
            sender: { events in
                _ = try await api.sendEvents(
                    EventBatchBody(installId: installId, platform: platform, appVersion: appVersion, events: events))
            },
            clock: clock,
            random: random,
            options: EventQueueOptions(
                maxSize: validated.maxQueuedEvents,
                flushDelay: validated.flushDelay,
                maxAge: validated.maxEventAge))
        self.queue = queue

        // `link_open` is never consent-gated (see `DleConfig.gateEventsOnAnalyticsConsent`).
        self.links = UniversalLinkHandler(linkHosts: validated.linkHosts) { event in
            queue.enqueue(event)
        }

        emitFirstOpenIfNeeded()
        startForegroundFlushing()
    }

    /// The installation identifier: a random UUID v4 created on first launch and kept in the
    /// Keychain. Survives app updates, not reinstalls. Never derived from the device.
    public var installId: String {
        installIdLoaded.id
    }

    /// The consent currently in force.
    public var consent: DleConsent {
        consentBox.current
    }

    /// The persisted answer of the once-per-installation resolve, or `nil` before it happened.
    public var deferredLink: DeferredLink? {
        stateStore.deferredLink
    }

    /// `true` once a resolve result has been obtained and persisted for this installation.
    public var isResolved: Bool {
        stateStore.deferredLink != nil
    }

    // MARK: - Resolve

    /// Recovers the context of the click that led to this installation (UC-04, UC-05).
    ///
    /// * The first call posts `POST /v1/resolve` and persists the answer; later calls return
    ///   the persisted answer without a request, for the life of the installation (TC-143). The
    ///   one exception: a non-final answer (`expires_in > 0`) may be requested again after it
    ///   has expired.
    /// * A `claimCode` or `loginKey` is a deterministic supplement (S3, S2): it is sent even
    ///   after the first resolve, unless the persisted answer is already deterministic. The code
    ///   is normalised with ``DleClaimCode/normalize(_:)`` and checked locally first.
    /// * Concurrent calls are serialised; nothing is ever sent twice for the same reason.
    /// * Device signals are attached only under ``DleDeferredStrategies/probabilistic`` **and**
    ///   ``DleConsent/allowsDeviceSignals``; otherwise `signals` is absent from the body.
    ///
    /// Read ``DeferredLink/isDeterministic`` before doing anything irreversible with the result.
    ///
    /// - Parameters:
    ///   - claimCode: The text the user typed from the interstitial page, if any.
    ///   - loginKey: An opaque, already hashed account identifier, if the user signed in.
    /// - Throws: ``DleError/strategyDisabled(_:)``, ``DleError/claimCodeMalformed``,
    ///   ``DleError/claimCodeRejected(reason:canReissue:)``, ``DleError/installIdUnavailable``,
    ///   and the transport and HTTP failures of ``DleError``.
    public func resolve(claimCode: String? = nil, loginKey: String? = nil) async throws -> DeferredLink {
        let code: String? = try claimCode.map { raw in
            guard config.deferredStrategies.contains(.claimCode) else {
                throw DleError.strategyDisabled(.claimCode)
            }
            let normalized = DleClaimCode.normalize(raw)
            guard DleClaimCode.isWellFormed(normalized) else { throw DleError.claimCodeMalformed }
            return normalized
        }
        let key: String? = try loginKey.flatMap { raw in
            let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !trimmed.isEmpty else { return nil }
            guard config.deferredStrategies.contains(.login) else {
                throw DleError.strategyDisabled(.login)
            }
            return trimmed
        }
        return try await resolves.run { [self] in
            try await performResolve(claimCode: code, loginKey: key)
        }
    }

    /// Submits the claim code the user typed (S3, FR-184). Equivalent to
    /// ``resolve(claimCode:loginKey:)`` with `claimCode` only.
    public func submitClaimCode(_ code: String) async throws -> DeferredLink {
        try await resolve(claimCode: code)
    }

    /// Reconciles this installation with the click that carried the same account key (S2,
    /// FR-185). `loginKey` must be opaque and already hashed; never pass an e-mail address or a
    /// raw user id. Equivalent to ``resolve(claimCode:loginKey:)`` with `loginKey` only.
    public func reconcileLogin(loginKey: String) async throws -> DeferredLink {
        try await resolve(loginKey: loginKey)
    }

    private func performResolve(claimCode: String?, loginKey: String?) async throws -> DeferredLink {
        let supplemental = claimCode != nil || loginKey != nil
        let persisted = stateStore.current
        if let existing = persisted.deferredLink {
            if supplemental {
                if existing.isDeterministic {
                    DleLog.debug("resolve: already deterministic, nothing to add")
                    return existing
                }
            } else if existing.isFinal || !Self.hasExpired(existing, resolvedAt: persisted.resolvedAt, now: clock.now) {
                return existing
            }
        }

        guard installIdLoaded.persisted else { throw DleError.installIdUnavailable }

        let consent = consentBox.current
        var signals: DeviceSignalsBody?
        if Self.shouldAttachSignals(strategies: config.deferredStrategies, consent: consent) {
            signals = await MainActor.run { DeviceSignalsCollector.collect() }
        }
        let body = Self.makeResolveBody(
            installId: installIdLoaded.id,
            appVersion: config.appVersion,
            osVersion: DlePlatform.osVersion(),
            claimCode: claimCode,
            loginKey: loginKey,
            signals: signals,
            consent: consent)

        let link = try await api.resolve(body)
        let now = clock.now
        stateStore.update { s in
            s.deferredLink = link
            s.resolvedAt = now
        }
        DleLog.info("resolved: matched=\(link.matched) match_type=\(link.matchType.rawValue) confidence=\(link.confidence)")
        return link
    }

    /// `true` when the probabilistic strategy is enabled **and** consent allows device signals.
    /// Both are required; neither alone turns collection on (spec §0.2, FR-227, TC-146).
    static func shouldAttachSignals(strategies: DleDeferredStrategies, consent: DleConsent) -> Bool {
        strategies.contains(.probabilistic) && consent.allowsDeviceSignals
    }

    /// `true` when a non-final result has outlived its `expires_in`.
    static func hasExpired(_ link: DeferredLink, resolvedAt: Date?, now: Date) -> Bool {
        guard !link.isFinal else { return false }
        guard let resolvedAt else { return true }
        return resolvedAt.addingTimeInterval(TimeInterval(link.expiresIn)) <= now
    }

    /// Assembles the `ResolveRequestDto` body. `platform` is always this build's wire name;
    /// `referrer` is never set on Apple platforms.
    static func makeResolveBody(
        installId: String,
        appVersion: String?,
        osVersion: String?,
        claimCode: String?,
        loginKey: String?,
        signals: DeviceSignalsBody?,
        consent: DleConsent
    ) -> ResolveRequestBody {
        ResolveRequestBody(
            installId: installId,
            platform: DlePlatform.wireName,
            appVersion: appVersion,
            osVersion: osVersion,
            referrer: nil,
            claimCode: claimCode,
            loginKey: loginKey,
            signals: signals,
            consent: ConsentBody(consent))
    }

    // MARK: - Events

    /// Queues an event for `POST /v1/events`.
    ///
    /// Behavioural events (`first_open`, `session`, `conversion`, `custom`) are dropped unless
    /// ``DleConsent/analytics`` is granted, while ``DleConfig/gateEventsOnAnalyticsConsent`` is
    /// on. `link_open` is never gated. Keep `properties` free of personal data.
    public func track(_ event: DleEvent) {
        if event.type.isBehavioural, config.gateEventsOnAnalyticsConsent, !consentBox.current.analytics {
            DleLog.debug("dropping \(event.type.rawValue) event: analytics consent not granted")
            return
        }
        queue.enqueue(event)
    }

    /// Sends the queued events now. Returns `true` when the queue is empty afterwards.
    @discardableResult
    public func flush() async -> Bool {
        await queue.flush()
    }

    /// Number of events waiting to be sent.
    public var pendingEventCount: Int {
        queue.count
    }

    /// Queues `first_open` once per installation. Called at start-up and whenever consent
    /// changes, so an installation whose user granted analytics consent after the first launch
    /// still reports it — once.
    private func emitFirstOpenIfNeeded() {
        guard !stateStore.current.firstOpenTracked else { return }
        if config.gateEventsOnAnalyticsConsent, !consentBox.current.analytics { return }
        stateStore.update { $0.firstOpenTracked = true }
        queue.enqueue(DleEvent(type: .firstOpen))
    }

    // MARK: - Links

    /// Handles a Universal Link continuation from `application(_:continue:restorationHandler:)`
    /// or `scene(_:continue:)`: reports `link_open` and returns the URL to route.
    ///
    /// - Returns: The web page URL, or `nil` when the activity is not a
    ///   `NSUserActivityTypeBrowsingWeb` continuation (return `false` to UIKit in that case).
    @discardableResult
    public func handle(_ activity: NSUserActivity) -> URL? {
        links.handle(activity)
    }

    /// Handles a URL from SwiftUI's `.onOpenURL`, `scene(_:openURLContexts:)` or
    /// `application(_:open:options:)`: reports `link_open` when it is an `http(s)` URL on one of
    /// ``DleConfig/linkHosts``, and returns the URL to route. A custom-scheme URL is returned
    /// unreported and is never logged in full.
    ///
    /// - Returns: The URL to route; `nil` only for a URL without a scheme.
    @discardableResult
    public func handle(url: URL) -> URL? {
        links.handle(url: url)
    }

    /// Handles the user activities of a cold start (`UIScene.ConnectionOptions.userActivities`).
    /// - Returns: The URL of the first Universal Link continuation among them, or `nil`.
    @discardableResult
    public func handle(userActivities: Set<NSUserActivity>) -> URL? {
        links.handle(userActivities: userActivities)
    }

    #if canImport(UIKit) && !os(watchOS)
    /// Handles the URL contexts of `scene(_:openURLContexts:)` or of a cold start.
    /// - Returns: The URLs to route.
    @MainActor
    @discardableResult
    public func handle(urlContexts: Set<UIOpenURLContext>) -> [URL] {
        links.handle(urlContexts: urlContexts)
    }
    #endif

    // MARK: - Consent

    /// Replaces the consent in force. Takes effect immediately: a later resolve attaches or
    /// omits signals accordingly, behavioural events are gated accordingly, and withdrawing
    /// analytics consent discards the behavioural events still waiting in the queue.
    public func updateConsent(_ consent: DleConsent) {
        let previous = consentBox.withLock { current -> DleConsent in
            defer { current = consent }
            return current
        }
        if config.gateEventsOnAnalyticsConsent, previous.analytics, !consent.analytics {
            queue.removeAll { $0.type.isBehavioural }
        }
        if consent.attribution, !previous.attribution {
            forgetUnmatchedAnswer()
        }
        DleLog.info("consent updated: analytics=\(consent.analytics) attribution=\(consent.attribution)")
        emitFirstOpenIfNeeded()
    }

    /// A resolve made before attribution consent was recorded answers `none` with a non-final
    /// `expires_in`: the engine could not link the click, not because there was none. Once consent
    /// arrives that answer is stale, so it is dropped and the next ``resolve(claimCode:loginKey:)``
    /// - the next launch, in the documented integration - asks again (TC-141).
    private func forgetUnmatchedAnswer() {
        let persisted = stateStore.current
        guard let existing = persisted.deferredLink, !existing.matched, !existing.isFinal else { return }
        stateStore.update { s in
            s.deferredLink = nil
            s.resolvedAt = nil
        }
        DleLog.info("cached unmatched answer dropped: attribution consent was recorded after it")
    }

    /// Alias of ``updateConsent(_:)``.
    public func setConsent(_ consent: DleConsent) {
        updateConsent(consent)
    }

    // MARK: - Lifecycle

    /// Stops the queue and removes the foreground observers. Used when the instance is replaced.
    func shutdown() {
        let tokens = observers.withLock { current -> [any NSObjectProtocol] in
            defer { current = [] }
            return current
        }
        for token in tokens {
            NotificationCenter.default.removeObserver(token)
        }
        queue.shutdown()
    }

    /// Notifications after which the queue is flushed. Spelled as raw names on purpose: the
    /// typed constants (`UIApplication.willEnterForegroundNotification`) are static members of
    /// main-actor classes and cannot be read from this non-isolated context in every SDK, while
    /// the underlying names are part of the platform ABI and identical. Both the application-
    /// and the scene-level notification are observed; the queue coalesces duplicate flushes.
    static let foregroundNotificationNames: [Notification.Name] = {
        #if canImport(UIKit) && !os(watchOS)
        return [
            Notification.Name("UIApplicationWillEnterForegroundNotification"),
            Notification.Name("UISceneWillEnterForegroundNotification"),
        ]
        #elseif canImport(AppKit)
        return [Notification.Name("NSApplicationWillBecomeActiveNotification")]
        #else
        return []
        #endif
    }()

    private func startForegroundFlushing() {
        guard config.flushOnForeground else { return }
        let tokens = Self.foregroundNotificationNames.map { name in
            NotificationCenter.default.addObserver(forName: name, object: nil, queue: nil) { [weak self] _ in
                self?.queue.requestFlush()
            }
        }
        observers.withLock { $0 = tokens }
    }
}

/// Serialises resolve calls: a call made while another is in flight waits for it, then runs —
/// so a claim code typed while the first-launch resolve is still on the wire is neither lost
/// nor sent in parallel with it.
actor ResolveSerializer {
    private var tail: Task<Void, Never>?

    /// Runs `body` after every previously submitted body has finished.
    func run<T: Sendable>(_ body: @escaping @Sendable () async throws -> T) async throws -> T {
        let previous = tail
        let task = Task<T, Error> {
            if let previous {
                await previous.value
            }
            return try await body()
        }
        tail = Task<Void, Never> { _ = try? await task.value }
        return try await task.value
    }
}

#if !canImport(Security)
/// Process-lifetime stand-in for the Keychain on platforms without the Security framework.
struct InMemoryKeychain: KeychainStorage {
    private static let items = Locked<[String: String]>([:])

    func read(service: String, account: String) throws -> String? {
        Self.items.current[service + "/" + account]
    }

    func write(_ value: String, service: String, account: String) throws {
        Self.items.withLock { $0[service + "/" + account] = value }
    }

    func delete(service: String, account: String) throws {
        Self.items.withLock { $0[service + "/" + account] = nil }
    }
}
#endif
