using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Dle.Crypto;

/// <summary>
/// ML-DSA-65 signer (<see cref="SignatureAlgorithms.MlDsa65"/>), the FIPS 204 lattice signature.
/// </summary>
/// <remarks>
/// BouncyCastle rather than the BCL, deliberately. §E.5.2: <c>System.Security.Cryptography.MLDsa</c>
/// ships in .NET 10 behind <c>[Experimental("SYSLIB5006")]</c> and delegates to the platform, which
/// means OpenSSL 3.5+ on Linux — absent from Ubuntu 24.04 LTS and RHEL 9. An open source product
/// that a stranger deploys on their own LTS box cannot make that a requirement, so the portable
/// managed implementation is the realistic one.
/// </remarks>
public sealed class MlDsa65Signer : ISigner
{
    /// <summary>Length of the seed a private key is derived from, in bytes (FIPS 204 ξ).</summary>
    public const int SeedSize = 32;

    /// <summary>Length of a public key in bytes.</summary>
    public const int PublicKeySize = 1952;

    /// <summary>Length of a signature in bytes.</summary>
    public const int SignatureSize = 3309;

    private readonly MLDsaPrivateKeyParameters _privateKey;

    /// <summary>
    /// Creates a signer.
    /// </summary>
    /// <param name="keyId">Identifier written into the token.</param>
    /// <param name="seed">The 32 byte key generation seed. Storing the seed rather than the
    /// expanded key keeps the private material small enough to sit in a configuration value or an
    /// encrypted column without special handling.</param>
    /// <exception cref="ArgumentException"><paramref name="keyId"/> is empty or
    /// <paramref name="seed"/> is not <see cref="SeedSize"/> bytes long.</exception>
    public MlDsa65Signer(string keyId, ReadOnlySpan<byte> seed)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyId);

        if (seed.Length != SeedSize)
        {
            throw new ArgumentException("An ML-DSA-65 seed is 32 bytes.", nameof(seed));
        }

        KeyId = keyId;
        _privateKey = MLDsaPrivateKeyParameters.FromSeed(MLDsaParameters.ml_dsa_65, seed.ToArray());
    }

    /// <inheritdoc />
    public string AlgorithmId => SignatureAlgorithms.MlDsa65;

    /// <inheritdoc />
    public string KeyId { get; }

    /// <inheritdoc />
    public int MaxSignatureSize => SignatureSize;

    /// <summary>Returns the matching public key, for publication through JWKS.</summary>
    /// <returns>The encoded public key.</returns>
    public byte[] GetPublicKey() => _privateKey.GetPublicKeyEncoded();

    /// <inheritdoc />
    public byte[] Sign(ReadOnlySpan<byte> payload)
    {
        // Deterministic signing: the same payload and key always give the same signature, which
        // removes the nonce reuse failure mode and makes a signature reproducible in a test.
        MLDsaSigner signer = new(MLDsaParameters.ml_dsa_65, deterministic: true);
        signer.Init(forSigning: true, _privateKey);
        signer.BlockUpdate(payload);

        return signer.GenerateSignature();
    }
}
