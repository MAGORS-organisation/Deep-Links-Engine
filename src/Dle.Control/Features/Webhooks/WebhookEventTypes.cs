using System.Globalization;
using System.Text.Json;

using Dle.Domain.Contracts;

namespace Dle.Control.Features.Webhooks;

/// <summary>
/// The event types a subscription may ask for, and the envelope they are delivered in (§B.7.4).
/// </summary>
/// <remarks>
/// The set is closed and validated at subscription time. A subscription to an event type that does
/// not exist is a silent integration failure: the customer waits for callbacks that never come and
/// blames the engine, which is a worse outcome than a validation error at creation.
/// </remarks>
public static class WebhookEventTypes
{
    /// <summary>An install was attributed to a click (FR-204).</summary>
    public const string AttributionCreated = "attribution.created";

    /// <summary>A link was created.</summary>
    public const string LinkCreated = "link.created";

    /// <summary>A link's configuration changed.</summary>
    public const string LinkUpdated = "link.updated";

    /// <summary>A link was withdrawn from service following an abuse decision (TC-103).</summary>
    public const string LinkQuarantined = "link.quarantined";

    /// <summary>A quarantined link was returned to service after a successful appeal.</summary>
    public const string LinkReleased = "link.released";

    /// <summary>A domain's association files stopped verifying (FR-143, §C.8).</summary>
    public const string DomainVerificationFailed = "domain.verification_failed";

    /// <summary>A delivery produced by the test endpoint, never by a real event.</summary>
    public const string Test = "webhook.test";

    /// <summary>Every event type a subscription may name.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        AttributionCreated,
        LinkCreated,
        LinkUpdated,
        LinkQuarantined,
        LinkReleased,
        DomainVerificationFailed,
        Test,
    ];

    /// <summary>Whether a subscription may name this event type.</summary>
    /// <param name="eventType">The requested event type.</param>
    /// <returns><see langword="true"/> when the type is known.</returns>
    public static bool IsKnown(string? eventType)
    {
        if (string.IsNullOrWhiteSpace(eventType))
        {
            return false;
        }

        foreach (string known in All)
        {
            if (string.Equals(eventType, known, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Renders the envelope that is stored in the outbox and later signed and sent verbatim.
    /// </summary>
    /// <param name="eventType">One of the constants on this class.</param>
    /// <param name="id">Delivery identifier, which the receiver uses for replay protection.</param>
    /// <param name="occurredAt">When the underlying event happened.</param>
    /// <param name="data">Event payload.</param>
    /// <returns>The JSON document, as the exact string that will be transmitted.</returns>
    /// <remarks>
    /// Rendered once, at the moment the event is recorded, and stored as text. Re-rendering it at
    /// delivery time would risk producing different bytes — a reordered member, a different
    /// timestamp precision — and the signature covers exactly the bytes sent, so different bytes
    /// mean a signature the receiver cannot verify (TC-165).
    /// </remarks>
    public static string Render(
        string eventType,
        Guid id,
        DateTimeOffset occurredAt,
        IReadOnlyDictionary<string, string> data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentNullException.ThrowIfNull(data);

        WebhookPayload payload = new()
        {
            Event = eventType,
            Id = id.ToString("D", CultureInfo.InvariantCulture),
            OccurredAt = occurredAt,
            Data = data,
        };

        return JsonSerializer.Serialize(payload, WebhookJsonContext.Default.WebhookPayload);
    }
}
