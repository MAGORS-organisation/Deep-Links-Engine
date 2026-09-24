using System.ComponentModel.DataAnnotations;

namespace Dle.Crypto;

/// <summary>
/// One signing key supplied through configuration, for deployments that manage key material
/// outside the database. Bound from <c>Dle:Crypto:Keys</c>.
/// </summary>
/// <remarks>
/// Configuration keys are the bootstrap path: they are always available synchronously, so the key
/// ring can serve a signer before any I/O has happened. A deployment that rotates keys through the
/// <c>signing_keys</c> table adds them on top through <see cref="ISigningKeyStore"/>; the two
/// sources are merged, never exclusive.
/// </remarks>
public sealed class SigningKeyOptions
{
    /// <summary>Key identifier written into every token. Must not contain a dot, which separates
    /// the segments of the token format.</summary>
    [Required]
    [RegularExpression("^[A-Za-z0-9_-]{1,64}$")]
    public string Kid { get; set; } = string.Empty;

    /// <summary>Algorithm identifier, one of the constants on
    /// <see cref="SignatureAlgorithms"/>.</summary>
    [Required]
    public string Algorithm { get; set; } = string.Empty;

    /// <summary>Base64 encoded private key material. Its meaning is algorithm specific and is
    /// documented on <see cref="SigningKeyFactory"/>.</summary>
    [Required]
    public string PrivateKey { get; set; } = string.Empty;

    /// <summary>Base64 encoded public key material. Derived from the private key when omitted.</summary>
    public string? PublicKey { get; set; }

    /// <summary>Purpose, one of the constants on <see cref="SigningKeyPurposes"/>.</summary>
    [Required]
    public string Purpose { get; set; } = SigningKeyPurposes.Token;

    /// <summary>Start of the validity window, in UTC. Unbounded when omitted.</summary>
    public DateTimeOffset? NotBefore { get; set; }

    /// <summary>End of the validity window, in UTC. Unbounded when omitted.</summary>
    public DateTimeOffset? NotAfter { get; set; }

    /// <summary>Whether this key signs. At most one configured key per purpose may set it; when
    /// none does, the first valid key of the configured algorithm is used.</summary>
    public bool IsCurrent { get; set; }
}
