using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Dle.Crypto;

/// <summary>
/// SLH-DSA-SHA2-128s signer (<see cref="SignatureAlgorithms.SlhDsa128s"/>), the FIPS 205 hash
/// based signature.
/// </summary>
/// <remarks>
/// Its security rests only on the hash function, with none of the lattice assumptions behind
/// ML-DSA, which is what makes it the conservative choice for the one artefact class in §E.4.1
/// with a multi-year lifetime: release signing (K7). The price is bluntly stated so nobody
/// discovers it in production — the <c>s</c> parameter set trades signing speed for signature
/// size, so a signature takes on the order of a second to produce and is roughly 7.8 kB. It
/// belongs on artefacts signed by the hundred, never on request path tokens.
/// </remarks>
public sealed class SlhDsa128sSigner : ISigner
{
    /// <summary>Length of a private key in bytes.</summary>
    public const int PrivateKeySize = 64;

    /// <summary>Length of a public key in bytes.</summary>
    public const int PublicKeySize = 32;

    /// <summary>Length of a signature in bytes.</summary>
    public const int SignatureSize = 7856;

    private readonly SlhDsaPrivateKeyParameters _privateKey;

    /// <summary>
    /// Creates a signer.
    /// </summary>
    /// <param name="keyId">Identifier written into the token.</param>
    /// <param name="privateKey">The 64 byte encoded private key.</param>
    /// <exception cref="ArgumentException"><paramref name="keyId"/> is empty or
    /// <paramref name="privateKey"/> is not <see cref="PrivateKeySize"/> bytes long.</exception>
    public SlhDsa128sSigner(string keyId, ReadOnlySpan<byte> privateKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyId);

        if (privateKey.Length != PrivateKeySize)
        {
            throw new ArgumentException("An SLH-DSA-128s private key is 64 bytes.", nameof(privateKey));
        }

        KeyId = keyId;
        _privateKey = SlhDsaPrivateKeyParameters.FromEncoding(SlhDsaParameters.slh_dsa_sha2_128s, privateKey.ToArray());
    }

    /// <inheritdoc />
    public string AlgorithmId => SignatureAlgorithms.SlhDsa128s;

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
        SlhDsaSigner signer = new(SlhDsaParameters.slh_dsa_sha2_128s, deterministic: true);
        signer.Init(forSigning: true, _privateKey);
        signer.BlockUpdate(payload);

        return signer.GenerateSignature();
    }
}
