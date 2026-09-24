namespace Dle.Domain.Entities;

/// <summary>
/// A customer endpoint subscribed to postbacks (FR-204). Maps to <c>webhook_subscriptions</c>.
/// </summary>
public class WebhookSubscription
{
    /// <summary>Primary key, a UUIDv7.</summary>
    public Guid Id { get; set; }

    /// <summary>Owning tenant.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Endpoint the postbacks are delivered to.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Shared secret for the symmetric signature, encrypted at rest. The signature is
    /// doubled with an asymmetric one so that a third party can verify a delivery without ever
    /// receiving this value (§B.7.4).</summary>
    public byte[] SecretEncrypted { get; set; } = [];

    /// <summary>Event types the endpoint is interested in, for example <c>attribution.created</c>.</summary>
    public List<string> EventTypes { get; set; } = [];

    /// <summary>Whether deliveries are attempted.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Creation instant, in UTC.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
