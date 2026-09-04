namespace Dle.Domain.Entities;

/// <summary>
/// A control plane API key (FR-242). Maps to <c>api_keys</c>.
/// </summary>
/// <remarks>
/// Only the Argon2id hash of the key is stored, so a database dump does not yield working
/// credentials. The prefix is kept in clear text purely so that a key can be located without
/// hashing every row, and so that an operator can recognise which key is which in the list.
/// </remarks>
public class ApiKey
{
    /// <summary>Primary key, a UUIDv7.</summary>
    public Guid Id { get; set; }

    /// <summary>Owning tenant.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Name given by the operator, for describing what the key is for.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Public prefix of the key, used for lookup and for display.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>Argon2id hash of the secret. Compared in constant time, never with equality.</summary>
    public byte[] Hash { get; set; } = [];

    /// <summary>Role, stored as text: <c>owner</c>, <c>admin</c>, <c>editor</c> or <c>viewer</c>.</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>Additional fine grained scopes granted on top of the role.</summary>
    public List<string> Scopes { get; set; } = [];

    /// <summary>Last time the key was used, in UTC. Written lazily; it is an operational hint for
    /// finding unused keys, not an audit record.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>Expiry instant, in UTC.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Revocation instant, in UTC. A revoked key is kept so that the audit log keeps
    /// referring to something that still exists.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Creation instant, in UTC.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
