namespace Dle.Domain.Entities;

/// <summary>
/// One signing key in the ring. Maps to <c>signing_keys</c> (§E.4.2, ADR-013, K7 in §E.4.1).
/// </summary>
/// <remarks>
/// Rotation is the reason this is a table rather than a configuration value. The verifier accepts
/// every key whose validity window still covers the artefacts in circulation, while the signer uses
/// exactly one, so a key can be replaced without a window in which existing signatures stop
/// verifying (S-12). The algorithm is stored alongside the key because crypto agility is a design
/// commitment here: adding ML-DSA later must not change the token format, only the value of
/// <see cref="Algorithm"/>.
/// </remarks>
public class SigningKeyRecord
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>Owning tenant, or <see langword="null"/> for an instance wide key.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>Key identifier carried in every signature, so a verifier knows which key to use.</summary>
    public string Kid { get; set; } = string.Empty;

    /// <summary>Algorithm identifier, for example <c>Ed25519</c> or <c>Ed25519+MLDSA65</c>.</summary>
    public string Algorithm { get; set; } = string.Empty;

    /// <summary>Public key material, published through the JWKS endpoint.</summary>
    public byte[] PublicKey { get; set; } = [];

    /// <summary>
    /// Private key material, encrypted at rest. Never leaves the control plane: the edge resolver
    /// verifies but does not sign, so it never needs this (T-15).
    /// </summary>
    public byte[]? PrivateKeyEncrypted { get; set; }

    /// <summary>Purpose: <c>webhook</c>, <c>click_id</c>, <c>slug</c> or <c>token</c>.</summary>
    public string Purpose { get; set; } = string.Empty;

    /// <summary>Start of the validity window.</summary>
    public DateTimeOffset NotBefore { get; set; }

    /// <summary>End of the validity window. Signatures made before it stay verifiable after it.</summary>
    public DateTimeOffset? NotAfter { get; set; }

    /// <summary>Whether this is the key the signer currently uses. Exactly one per purpose.</summary>
    public bool IsCurrent { get; set; }

    /// <summary>Creation instant.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
