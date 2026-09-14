namespace Dle.Domain.Entities;

/// <summary>
/// A stored response for a replayed <c>Idempotency-Key</c>. Maps to <c>idempotency_records</c> (§B.7.3).
/// </summary>
/// <remarks>
/// A client that retries after a timeout must not create a second link. The key alone is not
/// enough to decide that, though: replaying the same key with a different body is a client bug, not
/// a retry, and answering it with the first response would silently hide the mistake. That is why
/// <see cref="RequestHash"/> is stored and compared, and a mismatch is rejected with
/// <see cref="Contracts.ProblemCodes.IdempotencyConflict"/>.
/// </remarks>
public class IdempotencyRecord
{
    /// <summary>The <c>Idempotency-Key</c> header value, scoped to the tenant.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Tenant the key belongs to. Keys never collide across tenants.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Endpoint the key was used against, so the same key on a different route conflicts.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Hash of the canonical request body, used to tell a retry from a client mistake.</summary>
    public byte[] RequestHash { get; set; } = [];

    /// <summary>HTTP status of the stored response.</summary>
    public int ResponseStatus { get; set; }

    /// <summary>Body of the stored response, replayed verbatim.</summary>
    public string? ResponseBody { get; set; }

    /// <summary>When the original request was handled.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When this record may be pruned. Retries beyond it are treated as new requests.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
