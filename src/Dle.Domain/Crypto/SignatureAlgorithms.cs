namespace Dle.Domain.Crypto;

/// <summary>
/// Identifiers of the signature algorithms the engine can use (§E.4.2). They appear verbatim in
/// the <c>alg</c> segment of a signed token and in the <c>DLE-Alg</c> webhook header, so they are
/// part of the public wire contract and must never be renamed.
/// </summary>
/// <remarks>
/// Crypto agility has to exist from day one. Retrofitting an algorithm identifier into a token
/// format that is already in circulation is a breaking change for every integrator, which is why
/// the format carries the algorithm and the key identifier from the first release even though
/// only one algorithm is used at a time (§E.4.2, ADR-013).
/// </remarks>
public static class SignatureAlgorithms
{
    /// <summary>HMAC with SHA-256. Symmetric, and the default for click tokens and webhook
    /// signatures (K2, K4). No post-quantum exposure: 256 bits leaves 128 after Grover.</summary>
    public const string Hs256 = "HS256";

    /// <summary>Ed25519. Asymmetric, so a third party can verify a webhook without holding a
    /// shared secret. Broken by Shor, hence the hybrid option below.</summary>
    public const string Ed25519 = "Ed25519";

    /// <summary>ML-DSA-65, the FIPS 204 lattice signature.</summary>
    public const string MlDsa65 = "MLDSA65";

    /// <summary>Composite of Ed25519 and ML-DSA-65: both signatures must verify. The migration
    /// target for long lived asymmetric signatures (§E.5.3).</summary>
    public const string Ed25519MlDsa65 = "Ed25519+MLDSA65";

    /// <summary>SLH-DSA-128s, the FIPS 205 hash based signature. Conservative and large; kept for
    /// artefacts whose verification key must outlive any lattice assumption.</summary>
    public const string SlhDsa128s = "SLHDSA128s";
}
