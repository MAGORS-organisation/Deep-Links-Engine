namespace Dle.Crypto;

/// <summary>
/// One key in the ring, with everything needed to sign with it, verify against it and publish it.
/// Mirrors the <c>signing_keys</c> row shape (§B.5.2).
/// </summary>
/// <remarks>
/// A class rather than a record: the key material is held in arrays, and record value equality
/// over an array compares references, which would be quietly wrong every time.
/// </remarks>
public sealed class SigningKeyMaterial
{
    /// <summary>Identifier carried in every signature. Must not contain a dot, which separates
    /// the segments of the token format.</summary>
    public required string Kid { get; init; }

    /// <summary>Algorithm identifier, one of the constants on
    /// <see cref="SignatureAlgorithms"/>.</summary>
    public required string AlgorithmId { get; init; }

    /// <summary>
    /// Public key material. For a symmetric algorithm this holds the same secret as
    /// <see cref="PrivateKey"/>, which is exactly why
    /// <see cref="VerificationKey.ToJsonWebKey"/> refuses to publish it.
    /// </summary>
    public required byte[] PublicKey { get; init; }

    /// <summary>
    /// Private key material, or empty on an instance that only verifies. The edge resolver
    /// verifies and never signs, so it never needs this (T-15).
    /// </summary>
    public byte[] PrivateKey { get; init; } = [];

    /// <summary>Purpose, one of the constants on <see cref="SigningKeyPurposes"/>.</summary>
    public string Purpose { get; init; } = SigningKeyPurposes.Token;

    /// <summary>Start of the validity window, in UTC. Unbounded when <see langword="null"/>.</summary>
    public DateTimeOffset? NotBefore { get; init; }

    /// <summary>End of the validity window, in UTC. Unbounded when <see langword="null"/>.</summary>
    public DateTimeOffset? NotAfter { get; init; }

    /// <summary>Whether this key is the one that signs.</summary>
    public bool IsCurrent { get; init; }

    /// <summary>Whether the key may be used at an instant.</summary>
    /// <param name="instant">The instant to test, in UTC.</param>
    /// <returns><see langword="true"/> when the instant falls inside the validity window.</returns>
    public bool IsValidAt(DateTimeOffset instant) =>
        (NotBefore is null || instant >= NotBefore.Value) &&
        (NotAfter is null || instant <= NotAfter.Value);

    /// <summary>
    /// Returns a copy that no longer signs and stops being accepted at a given instant.
    /// </summary>
    /// <param name="notAfter">End of the acceptance window. Rotation sets this in the future,
    /// never in the past: a signature made a moment before the rotation has to keep verifying
    /// (S-12).</param>
    /// <returns>The retired copy.</returns>
    public SigningKeyMaterial Retire(DateTimeOffset notAfter) => new()
    {
        Kid = Kid,
        AlgorithmId = AlgorithmId,
        PublicKey = PublicKey,
        PrivateKey = PrivateKey,
        Purpose = Purpose,
        NotBefore = NotBefore,
        NotAfter = NotAfter is null || NotAfter.Value > notAfter ? notAfter : NotAfter,
        IsCurrent = false,
    };
}
