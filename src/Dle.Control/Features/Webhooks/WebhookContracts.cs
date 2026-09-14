namespace Dle.Control.Features.Webhooks;

/// <summary>
/// Response to <c>POST /api/v1/webhooks</c>. The shared secret appears here and nowhere else
/// (§B.5.2, §E.4.1 K4).
/// </summary>
/// <remarks>
/// Shown exactly once, like an API key. The stored copy is encrypted and the engine needs it to
/// compute the <c>v1</c> signature, so it could in principle be handed back later — and
/// deliberately is not. A secret that can be re-read is a secret that leaks through every future
/// read path somebody adds; a customer who lost it rotates the subscription, which is one request.
/// </remarks>
public sealed record WebhookCreatedResponse
{
    /// <summary>Subscription identifier.</summary>
    public required Guid Id { get; init; }

    /// <summary>Destination URL.</summary>
    public required string Url { get; init; }

    /// <summary>Subscribed event types.</summary>
    public IReadOnlyList<string> EventTypes { get; init; } = [];

    /// <summary>Whether the subscription is active.</summary>
    public required bool IsActive { get; init; }

    /// <summary>Creation instant.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// The shared secret, base64 encoded, shown exactly once. Feed it to an HMAC-SHA-256 over
    /// <c>t + "." + body</c> to verify the <c>v1</c> member of <c>DLE-Signature</c>.
    /// </summary>
    public required string Secret { get; init; }

    /// <summary>
    /// Key identifier of the asymmetric slot at the moment of creation, so an integrator knows
    /// which key of <c>/.well-known/jwks.json</c> to look for while testing.
    /// </summary>
    public string? SigningKeyId { get; init; }
}

/// <summary>One recorded delivery attempt, as the customer sees it (FR-204).</summary>
/// <remarks>
/// The payload is included because a customer debugging a failed integration needs to see what was
/// sent; the signature is not, because it is reproducible from the payload and the secret and
/// echoing it back would put it in one more place.
/// </remarks>
public sealed record WebhookDeliveryResponse
{
    /// <summary>Delivery identifier, matching the <c>id</c> member of the payload envelope.</summary>
    public required Guid Id { get; init; }

    /// <summary>Subscription the delivery belongs to.</summary>
    public required Guid SubscriptionId { get; init; }

    /// <summary>Event type that was delivered.</summary>
    public required string EventType { get; init; }

    /// <summary>Delivery state: <c>pending</c>, <c>delivered</c>, <c>failed</c> or <c>dead</c>.</summary>
    public required string Status { get; init; }

    /// <summary>Attempts made so far.</summary>
    public required int Attempt { get; init; }

    /// <summary>HTTP status of the last attempt, when the endpoint answered.</summary>
    public int? ResponseCode { get; init; }

    /// <summary>Last error, truncated. Never carries request headers.</summary>
    public string? LastError { get; init; }

    /// <summary>When the next attempt is due.</summary>
    public DateTimeOffset? NextAttemptAt { get; init; }

    /// <summary>When the delivery was recorded.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the endpoint accepted it.</summary>
    public DateTimeOffset? DeliveredAt { get; init; }

    /// <summary>The exact body that was or will be sent.</summary>
    public string? Payload { get; init; }
}

/// <summary>Result of <c>POST /api/v1/webhooks/{id}/test</c> (§B.7.3).</summary>
/// <remarks>
/// Synchronous on purpose. The whole value of the test button is that the integrator presses it and
/// immediately learns whether their endpoint answered, what it answered, and how long it took —
/// queueing it and answering 202 would move the answer into a log they have not built yet.
/// </remarks>
public sealed record TestWebhookResponse
{
    /// <summary>Whether the endpoint accepted the delivery.</summary>
    public required bool Delivered { get; init; }

    /// <summary>Outcome: <c>delivered</c>, <c>failed</c> or <c>unsendable</c>.</summary>
    public required string Outcome { get; init; }

    /// <summary>HTTP status the endpoint returned, when it answered.</summary>
    public int? ResponseCode { get; init; }

    /// <summary>Short description of the failure, when there was one.</summary>
    public string? Error { get; init; }

    /// <summary>How long the attempt took, in milliseconds.</summary>
    public required int ElapsedMs { get; init; }

    /// <summary>The exact body that was signed and sent, so the integrator can replay the check.</summary>
    public required string Payload { get; init; }

    /// <summary>Key identifier named in the <c>v2</c> slot of the signature.</summary>
    public string? SigningKeyId { get; init; }
}
