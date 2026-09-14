using System.Buffers.Text;

using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

using BcEd25519Signer = Org.BouncyCastle.Crypto.Signers.Ed25519Signer;

namespace Dle.Crypto;

/// <summary>
/// The public half of one key in the ring: it verifies signatures and knows how to publish itself.
/// </summary>
/// <remarks>
/// Instances are immutable and hold no signing material, so they can be handed to a process that
/// must verify but must never sign, which is what the edge resolver does (T-15). Construction goes
/// through <see cref="SigningKeyFactory.CreateVerificationKey"/> so that key material is parsed in
/// exactly one place.
/// </remarks>
public sealed class VerificationKey
{
    private readonly byte[] _material;
    private readonly Ed25519PublicKeyParameters? _ed25519;
    private readonly MLDsaPublicKeyParameters? _mlDsa;
    private readonly SlhDsaPublicKeyParameters? _slhDsa;

    /// <summary>Parses the public material for one algorithm.</summary>
    /// <exception cref="ArgumentException">The material does not match the algorithm.</exception>
    /// <exception cref="NotSupportedException">The algorithm is not implemented here.</exception>
    internal VerificationKey(
        string kid,
        string algorithmId,
        ReadOnlySpan<byte> publicKey,
        DateTimeOffset? notBefore,
        DateTimeOffset? notAfter)
    {
        Kid = kid;
        AlgorithmId = algorithmId;
        NotBefore = notBefore;
        NotAfter = notAfter;
        _material = publicKey.ToArray();

        switch (algorithmId)
        {
            case SignatureAlgorithms.Hs256:
                if (publicKey.Length < 32)
                {
                    throw new ArgumentException("An HS256 key must be at least 32 bytes.", nameof(publicKey));
                }

                break;

            case SignatureAlgorithms.Ed25519:
                _ed25519 = ReadEd25519(publicKey);
                break;

            case SignatureAlgorithms.MlDsa65:
                _mlDsa = ReadMlDsa(publicKey);
                break;

            case SignatureAlgorithms.SlhDsa128s:
                _slhDsa = ReadSlhDsa(publicKey);
                break;

            case SignatureAlgorithms.Ed25519MlDsa65:
                if (!CompositeEncoding.TrySplit(publicKey, out ReadOnlySpan<byte> classical, out ReadOnlySpan<byte> postQuantum))
                {
                    throw new ArgumentException("Composite public key material is malformed.", nameof(publicKey));
                }

                _ed25519 = ReadEd25519(classical);
                _mlDsa = ReadMlDsa(postQuantum);
                break;

            default:
                throw new NotSupportedException(
                    "Signature algorithm " + algorithmId + " is not implemented by this provider.");
        }
    }

    /// <summary>Key identifier, matching the <c>kid</c> segment of a token.</summary>
    public string Kid { get; }

    /// <summary>Algorithm identifier this key verifies.</summary>
    public string AlgorithmId { get; }

    /// <summary>Start of the acceptance window, in UTC.</summary>
    public DateTimeOffset? NotBefore { get; }

    /// <summary>End of the acceptance window, in UTC.</summary>
    public DateTimeOffset? NotAfter { get; }

    /// <summary>Whether the key is accepted at an instant.</summary>
    /// <param name="instant">The instant to test, in UTC.</param>
    /// <returns><see langword="true"/> when the instant falls inside the acceptance window.</returns>
    public bool IsValidAt(DateTimeOffset instant) =>
        (NotBefore is null || instant >= NotBefore.Value) &&
        (NotAfter is null || instant <= NotAfter.Value);

    /// <summary>
    /// Verifies a signature. Never throws: it runs on untrusted input, and an exception here would
    /// turn a malformed signature into a different outcome than an invalid one.
    /// </summary>
    /// <param name="payload">The exact bytes that were signed.</param>
    /// <param name="signature">The signature to check.</param>
    /// <returns><see langword="true"/> only when the signature verifies under this key.</returns>
    public bool Verify(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature)
    {
        try
        {
            return AlgorithmId switch
            {
                SignatureAlgorithms.Hs256 => VerifyHmac(payload, signature),
                SignatureAlgorithms.Ed25519 => VerifyEd25519(payload, signature),
                SignatureAlgorithms.MlDsa65 => VerifyMlDsa(payload, signature),
                SignatureAlgorithms.SlhDsa128s => VerifySlhDsa(payload, signature),
                SignatureAlgorithms.Ed25519MlDsa65 => VerifyComposite(payload, signature),
                _ => false,
            };
        }
#pragma warning disable CA1031 // Default deny: every failure mode below is reported as "does not verify".
        catch (Exception)
#pragma warning restore CA1031
        {
            // SHARED-KERNEL section 17.9: a signature that makes a primitive throw is treated
            // exactly like one that does not verify, never as an error a caller might handle
            // differently. The exception carries no information a verifier is allowed to act on.
            return false;
        }
    }

    /// <summary>
    /// Renders the key for publication at <c>/.well-known/jwks.json</c>.
    /// </summary>
    /// <returns>The JWK, or <see langword="null"/> for a symmetric key. An HS256 key has no public
    /// half; publishing it would hand every reader the ability to mint signatures, so it is left
    /// out of the set rather than published as an <c>oct</c> key.</returns>
    public JsonWebKey? ToJsonWebKey()
    {
        if (string.Equals(AlgorithmId, SignatureAlgorithms.Hs256, StringComparison.Ordinal))
        {
            return null;
        }

        bool isEd25519 = string.Equals(AlgorithmId, SignatureAlgorithms.Ed25519, StringComparison.Ordinal);

        return new JsonWebKey
        {
            Kid = Kid,

            // "AKP" is the key type the IETF JOSE post-quantum drafts use for algorithms that have
            // no curve. The raw key also goes into PublicKey, because no registration is final and
            // a strict consumer has to be able to ignore what it does not recognise.
            Kty = isEd25519 ? "OKP" : "AKP",
            Alg = AlgorithmId,
            Use = "sig",
            Crv = isEd25519 ? "Ed25519" : null,
            X = isEd25519 ? Base64Url.EncodeToString(_material) : null,
            PublicKey = isEd25519 ? null : Base64Url.EncodeToString(_material),
            NotBefore = NotBefore,
            NotAfter = NotAfter,
        };
    }

    private static Ed25519PublicKeyParameters ReadEd25519(ReadOnlySpan<byte> material)
    {
        if (material.Length != Ed25519Signer.PublicKeySize)
        {
            throw new ArgumentException("An Ed25519 public key is 32 bytes.", nameof(material));
        }

        return new Ed25519PublicKeyParameters(material);
    }

    private static MLDsaPublicKeyParameters ReadMlDsa(ReadOnlySpan<byte> material) =>
        MLDsaPublicKeyParameters.FromEncoding(MLDsaParameters.ml_dsa_65, material.ToArray());

    private static SlhDsaPublicKeyParameters ReadSlhDsa(ReadOnlySpan<byte> material) =>
        SlhDsaPublicKeyParameters.FromEncoding(SlhDsaParameters.slh_dsa_sha2_128s, material.ToArray());

    /// <summary>
    /// Recomputes the tag and compares it in constant time. SHARED-KERNEL section 17.6 admits no
    /// exception here: an equality comparison leaks the tag one byte at a time (T-17, S-03).
    /// </summary>
    private bool VerifyHmac(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature)
    {
        Span<byte> expected = stackalloc byte[SHA256.HashSizeInBytes];
        HMACSHA256.HashData(_material, payload, expected);

        return CryptographicOperations.FixedTimeEquals(expected, signature);
    }

    private bool VerifyEd25519(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature)
    {
        if (_ed25519 is null || signature.Length != Ed25519Signer.SignatureSize)
        {
            return false;
        }

        BcEd25519Signer verifier = new();
        verifier.Init(forSigning: false, _ed25519);
        verifier.BlockUpdate(payload);

        return verifier.VerifySignature(signature.ToArray());
    }

    private bool VerifyMlDsa(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature)
    {
        if (_mlDsa is null)
        {
            return false;
        }

        MLDsaSigner verifier = new(MLDsaParameters.ml_dsa_65, deterministic: false);
        verifier.Init(forSigning: false, _mlDsa);
        verifier.BlockUpdate(payload);

        return verifier.VerifySignature(signature.ToArray());
    }

    private bool VerifySlhDsa(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature)
    {
        if (_slhDsa is null)
        {
            return false;
        }

        SlhDsaSigner verifier = new(SlhDsaParameters.slh_dsa_sha2_128s, deterministic: false);
        verifier.Init(forSigning: false, _slhDsa);
        verifier.BlockUpdate(payload);

        return verifier.VerifySignature(signature.ToArray());
    }

    private bool VerifyComposite(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature)
    {
        if (!CompositeEncoding.TrySplit(signature, out ReadOnlySpan<byte> classical, out ReadOnlySpan<byte> postQuantum))
        {
            return false;
        }

        bool classicalOk = VerifyEd25519(payload, classical);
        bool postQuantumOk = VerifyMlDsa(payload, postQuantum);

        // Non short circuiting on purpose: both halves are always evaluated, so the time taken
        // does not reveal which of the two failed.
        return classicalOk & postQuantumOk;
    }
}
