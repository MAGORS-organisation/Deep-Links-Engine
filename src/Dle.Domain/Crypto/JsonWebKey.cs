namespace Dle.Domain.Crypto;

/// <summary>
/// One public key published at <c>/.well-known/jwks.json</c> so that integrators can verify
/// webhook signatures without holding a shared secret (§B.7.3, §B.7.4).
/// </summary>
/// <remarks>
/// Only public material appears here. The post-quantum algorithms have no registered JWK
/// representation yet, which is why <see cref="PublicKey"/> exists next to the standard
/// <see cref="X"/>: rather than invent a curve name, the raw key is published in a clearly
/// non standard field that a strict JWK consumer will ignore.
/// </remarks>
public sealed record JsonWebKey
{
    /// <summary>Key identifier, matching the <c>kid</c> segment of a signed token.</summary>
    public required string Kid { get; init; }

    /// <summary>Key type, for example <c>OKP</c> for Ed25519 or <c>oct</c> for a symmetric key.</summary>
    public required string Kty { get; init; }

    /// <summary>Algorithm the key is used with, one of the constants on
    /// <see cref="SignatureAlgorithms"/>.</summary>
    public required string Alg { get; init; }

    /// <summary>Intended use. Always <c>sig</c> for the keys this engine publishes.</summary>
    public required string Use { get; init; }

    /// <summary>Curve name for elliptic curve keys, for example <c>Ed25519</c>.</summary>
    public string? Crv { get; init; }

    /// <summary>Base64url encoded public coordinate of an elliptic curve key.</summary>
    public string? X { get; init; }

    /// <summary>Base64url encoded raw public key, for post-quantum algorithms that have no JWK
    /// registration.</summary>
    public string? PublicKey { get; init; }

    /// <summary>Instant from which the key may be used, in UTC. A key is published before it
    /// starts signing so that verifiers have time to fetch it.</summary>
    public DateTimeOffset? NotBefore { get; init; }

    /// <summary>Instant after which the key must no longer be accepted, in UTC. A retired key
    /// stays published until every signature it produced has expired.</summary>
    public DateTimeOffset? NotAfter { get; init; }
}
