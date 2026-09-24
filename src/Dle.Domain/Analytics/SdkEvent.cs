namespace Dle.Domain.Analytics;

/// <summary>
/// A single event reported by an SDK. Without these events a direct application open is
/// invisible to the engine, which systematically under-reports the most successful
/// campaigns (specification §B.6.4).
/// </summary>
public sealed record SdkEvent
{
    /// <summary>Event identifier, a time ordered UUIDv7.</summary>
    public required Guid Id { get; init; }

    /// <summary>Instant the event happened on the device, in UTC.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Owning tenant.</summary>
    public required Guid TenantId { get; init; }

    /// <summary>Application the reporting SDK belongs to.</summary>
    public required Guid AppId { get; init; }

    /// <summary>SDK generated identifier, stable for the lifetime of one installation.</summary>
    public required string InstallId { get; init; }

    /// <summary>Event kind.</summary>
    public required SdkEventType Type { get; init; }

    /// <summary>Event name, required for <see cref="SdkEventType.Custom"/> and conversions.</summary>
    public string? Name { get; init; }

    /// <summary>The link URL that opened the application, for <see cref="SdkEventType.LinkOpen"/>.</summary>
    public string? Url { get; init; }

    /// <summary>Monetary value of a conversion.</summary>
    public decimal? Value { get; init; }

    /// <summary>ISO-4217 currency code belonging to <see cref="Value"/>.</summary>
    public string? Currency { get; init; }

    /// <summary>Link the event was attributed to, when known.</summary>
    public long? LinkId { get; init; }

    /// <summary>Click identifier the event was attributed to, when known.</summary>
    public string? ClickId { get; init; }

    /// <summary>Additional event properties supplied by the application.</summary>
    public IReadOnlyDictionary<string, string>? Properties { get; init; }
}
