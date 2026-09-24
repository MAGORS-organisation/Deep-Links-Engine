namespace Dle.Domain.Entities;

/// <summary>
/// One delivery attempt of a postback, with its retry state (FR-204). Maps to
/// <c>webhook_deliveries</c>.
/// </summary>
/// <remarks>
/// The payload is stored with the delivery rather than rebuilt on retry. A retry has to send byte
/// for byte what the original signature covered, and regenerating the JSON would risk a different
/// property order and a signature the customer cannot verify.
/// </remarks>
public class WebhookDelivery
{
    /// <summary>Primary key, a UUIDv7.</summary>
    public Guid Id { get; set; }

    /// <summary>Owning tenant.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Subscription this delivery belongs to.</summary>
    public Guid SubscriptionId { get; set; }

    /// <summary>Event type being delivered.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>The exact body that was or will be sent, stored as a JSON document.</summary>
    public string Payload { get; set; } = "{}";

    /// <summary>Number of attempts made so far.</summary>
    public int Attempt { get; set; }

    /// <summary>Delivery state, stored as text: <c>pending</c>, <c>delivered</c>, <c>failed</c>
    /// or <c>dead</c> for an entry that exhausted its retries.</summary>
    public string Status { get; set; } = "pending";

    /// <summary>When the next attempt is due, in UTC, following exponential backoff.</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>Last error, truncated. Never contains the request headers, which carry the
    /// signature and any customer credentials.</summary>
    public string? LastError { get; set; }

    /// <summary>HTTP status code of the last attempt.</summary>
    public int? ResponseCode { get; set; }

    /// <summary>Creation instant, in UTC.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Instant of successful delivery, in UTC.</summary>
    public DateTimeOffset? DeliveredAt { get; set; }
}
