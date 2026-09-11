import Foundation

/// Kind of a client event. Raw values are the wire names accepted by `POST /v1/events`
/// (`SdkEventNames`, spec §B.7.2).
public enum DleEventType: String, Sendable, Hashable, CaseIterable, Codable {
    /// The application was opened through one of the engine's links (spec §B.6.4, FR-223).
    /// This is the event that makes a direct open measurable at all.
    case linkOpen = "link_open"
    /// First launch after an installation.
    case firstOpen = "first_open"
    /// A foreground session started.
    case session = "session"
    /// A conversion, optionally carrying a monetary value.
    case conversion = "conversion"
    /// Anything the host application defines for itself.
    case custom = "custom"

    /// `true` for the types that describe user behaviour and are therefore gated on
    /// ``DleConsent/analytics``. `link_open` is not: it reports a URL the operating system
    /// handed to the app, which the engine treats as a first-party observation.
    public var isBehavioural: Bool {
        self != .linkOpen
    }
}

/// A single event for `POST /v1/events`. Mirrors `EventDto` member for member.
///
/// Keep `properties` free of personal data: the engine stores them as given.
public struct DleEvent: Sendable, Hashable, Codable {
    /// Event type.
    public var type: DleEventType
    /// Name of a conversion or custom event, for example `purchase`.
    public var name: String?
    /// URL that opened the application, for a `link_open` event.
    public var url: String?
    /// Monetary value of a conversion. Non-finite values are not sent.
    public var value: Double?
    /// ISO 4217 currency code accompanying ``value``, for example `EUR`.
    public var currency: String?
    /// When the event happened on the device. Filled in at enqueue time when `nil`.
    public var timestamp: Date?
    /// Flat string properties.
    public var properties: [String: String]?

    /// Creates an event.
    public init(
        type: DleEventType,
        name: String? = nil,
        url: String? = nil,
        value: Double? = nil,
        currency: String? = nil,
        timestamp: Date? = nil,
        properties: [String: String]? = nil
    ) {
        self.type = type
        self.name = name
        self.url = url
        self.value = value
        self.currency = currency
        self.timestamp = timestamp
        self.properties = properties
    }

    /// A `link_open` report for a URL the operating system handed to the app (FR-223).
    /// The fragment is dropped: it never reaches a server and can carry client-side state.
    public static func linkOpen(url: URL, at timestamp: Date? = nil) -> DleEvent {
        DleEvent(type: .linkOpen, url: DleUniversalLink.reportable(url), timestamp: timestamp)
    }

    /// A `session` event.
    public static func session(at timestamp: Date? = nil, properties: [String: String]? = nil) -> DleEvent {
        DleEvent(type: .session, timestamp: timestamp, properties: properties)
    }

    /// A `conversion` event.
    public static func conversion(
        name: String,
        value: Double? = nil,
        currency: String? = nil,
        at timestamp: Date? = nil,
        properties: [String: String]? = nil
    ) -> DleEvent {
        DleEvent(type: .conversion, name: name, value: value, currency: currency, timestamp: timestamp, properties: properties)
    }

    /// A `custom` event.
    public static func custom(name: String, at timestamp: Date? = nil, properties: [String: String]? = nil) -> DleEvent {
        DleEvent(type: .custom, name: name, timestamp: timestamp, properties: properties)
    }

    private enum CodingKeys: String, CodingKey {
        case type, name, url, value, currency, ts, properties
    }

    /// Encodes the `EventDto` shape: snake_case keys in contract order, absent members omitted
    /// rather than written as `null`, `ts` as an ISO 8601 string.
    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(type, forKey: .type)
        try c.encodeIfPresent(name, forKey: .name)
        try c.encodeIfPresent(url, forKey: .url)
        if let value, value.isFinite {
            try c.encode(value, forKey: .value)
        }
        try c.encodeIfPresent(currency, forKey: .currency)
        if let timestamp {
            try c.encode(DleWireDate.string(from: timestamp), forKey: .ts)
        }
        try c.encodeIfPresent(properties, forKey: .properties)
    }

    /// Decodes the `EventDto` shape. An unparsable `ts` becomes `nil` ("on arrival").
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        type = try c.decode(DleEventType.self, forKey: .type)
        name = try c.decodeIfPresent(String.self, forKey: .name)
        url = try c.decodeIfPresent(String.self, forKey: .url)
        value = try c.decodeIfPresent(Double.self, forKey: .value)
        currency = try c.decodeIfPresent(String.self, forKey: .currency)
        if let raw = try c.decodeIfPresent(String.self, forKey: .ts) {
            timestamp = DleWireDate.date(from: raw)
        } else {
            timestamp = nil
        }
        properties = try c.decodeIfPresent([String: String].self, forKey: .properties)
    }
}
