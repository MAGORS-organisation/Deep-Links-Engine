using Org.BouncyCastle.Crypto.Parameters;

using BcEd25519Signer = Org.BouncyCastle.Crypto.Signers.Ed25519Signer;

namespace Dle.Crypto;

/// <summary>
/// Ed25519 signer (<see cref="SignatureAlgorithms.Ed25519"/>), the asymmetric slot that lets a
/// webhook receiver verify without holding a shared secret (K4).
/// </summary>
/// <remarks>
/// .NET 10 has no Ed25519 API: <c>System.Security.Cryptography</c> gained ML-KEM, ML-DSA, SLH-DSA
/// and Composite ML-DSA, but EdDSA is still absent, and the only occurrence of the name in the
/// framework is the <c>MLDsa44WithEd25519</c> composite parameter set. BouncyCastle is therefore
/// not a fallback here but the implementation, which also matches §E.5.2: BouncyCastle carries its
/// own primitives and does not require OpenSSL 3.5+ on the host, so the engine runs on the LTS
/// distributions a self-hoster actually has.
/// </remarks>
public sealed class Ed25519Signer : ISigner
{
    /// <summary>Length of a private key in bytes.</summary>
    public const int PrivateKeySize = 32;

    /// <summary>Length of a public key in bytes.</summary>
    public const int PublicKeySize = 32;

    /// <summary>Length of a signature in bytes.</summary>
    public const int SignatureSize = 64;

    private readonly Ed25519PrivateKeyParameters _privateKey;

    /// <summary>
    /// Creates a signer.
    /// </summary>
    /// <param name="keyId">Identifier written into the token.</param>
    /// <param name="privateKey">The 32 byte private key.</param>
    /// <exception cref="ArgumentException"><paramref name="keyId"/> is empty or
    /// <paramref name="privateKey"/> is not <see cref="PrivateKeySize"/> bytes long.</exception>
    public Ed25519Signer(string keyId, ReadOnlySpan<byte> privateKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyId);

        if (privateKey.Length != PrivateKeySize)
        {
            throw new ArgumentException("An Ed25519 private key is 32 bytes.", nameof(privateKey));
        }

        KeyId = keyId;
        _privateKey = new Ed25519PrivateKeyParameters(privateKey);
    }

    /// <inheritdoc />
    public string AlgorithmId => SignatureAlgorithms.Ed25519;

    /// <inheritdoc />
    public string KeyId { get; }

    /// <inheritdoc />
    public int MaxSignatureSize => SignatureSize;

    /// <summary>Returns the matching public key, for publication through JWKS.</summary>
    /// <returns>The 32 byte public key.</returns>
    public byte[] GetPublicKey() => _privateKey.GeneratePublicKey().GetEncoded();

    /// <inheritdoc />
    public byte[] Sign(ReadOnlySpan<byte> payload)
    {
        // The BouncyCastle signer accumulates state, so a fresh instance per signature is what
        // makes this type safe to share between requests.
        BcEd25519Signer signer = new();
        signer.Init(forSigning: true, _privateKey);
        signer.BlockUpdate(payload);

        return signer.GenerateSignature();
    }
}
