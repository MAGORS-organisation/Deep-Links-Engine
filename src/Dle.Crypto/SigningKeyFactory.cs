using System.Buffers.Text;

using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace Dle.Crypto;

/// <summary>
/// Generates key material, and turns key material into a signer or a verification key.
/// </summary>
/// <remarks>
/// <para>
/// Every algorithm specific decision about the shape of key material lives here, so that a new
/// algorithm is one <c>case</c> in three switches rather than a change to the token format. That
/// is the whole of the crypto agility promise in ADR-013 and §E.4.2: the format already carries
/// <c>alg</c> and <c>kid</c>, so adding ML-DSA later costs days rather than a breaking change for
/// every integrator.
/// </para>
/// <para>
/// Private key encodings, by algorithm:
/// <list type="bullet">
/// <item><description><c>HS256</c>: the 32 byte HMAC secret. The public half is the same value and
/// is never published.</description></item>
/// <item><description><c>Ed25519</c>: the 32 byte private key; the public key is derived.</description></item>
/// <item><description><c>MLDSA65</c>: the 32 byte FIPS 204 key generation seed rather than the
/// expanded key, so private material stays small enough for a configuration value or an encrypted
/// column.</description></item>
/// <item><description><c>SLHDSA128s</c>: the 64 byte encoded private key, which is what FIPS 205
/// defines and BouncyCastle exposes.</description></item>
/// <item><description><c>Ed25519+MLDSA65</c>: both of the above, framed by
/// <see cref="CompositeEncoding"/>.</description></item>
/// </list>
/// </para>
/// </remarks>
public static class SigningKeyFactory
{
    /// <summary>Number of random bytes behind a generated key identifier.</summary>
    private const int KeyIdEntropyBytes = 9;

    private static readonly string[] SupportedAlgorithmIds =
    [
        SignatureAlgorithms.Hs256,
        SignatureAlgorithms.Ed25519,
        SignatureAlgorithms.MlDsa65,
        SignatureAlgorithms.Ed25519MlDsa65,
        SignatureAlgorithms.SlhDsa128s,
    ];

    private static readonly string[] PostQuantumAlgorithmIds =
    [
        SignatureAlgorithms.MlDsa65,
        SignatureAlgorithms.Ed25519MlDsa65,
        SignatureAlgorithms.SlhDsa128s,
    ];

    /// <summary>Algorithms this provider implements.</summary>
    public static IReadOnlyCollection<string> SupportedAlgorithms => SupportedAlgorithmIds;

    /// <summary>Algorithms gated behind <see cref="CryptoOptions.HybridPqEnabled"/> (§E.5.3).</summary>
    public static IReadOnlyCollection<string> PostQuantumAlgorithms => PostQuantumAlgorithmIds;

    /// <summary>Whether an algorithm identifier is implemented here.</summary>
    /// <param name="algorithmId">The identifier to test.</param>
    /// <returns><see langword="true"/> when the algorithm can be signed and verified.</returns>
    public static bool IsSupported(string? algorithmId) =>
        algorithmId is not null && Array.IndexOf(SupportedAlgorithmIds, algorithmId) >= 0;

    /// <summary>Whether an algorithm carries a post-quantum component.</summary>
    /// <param name="algorithmId">The identifier to test.</param>
    /// <returns><see langword="true"/> when the algorithm is one of the phase 1 algorithms.</returns>
    public static bool IsPostQuantum(string? algorithmId) =>
        algorithmId is not null && Array.IndexOf(PostQuantumAlgorithmIds, algorithmId) >= 0;

    /// <summary>
    /// Generates a fresh key.
    /// </summary>
    /// <param name="algorithmId">Algorithm to generate for.</param>
    /// <param name="purpose">Purpose, one of the constants on <see cref="SigningKeyPurposes"/>.</param>
    /// <param name="notBefore">Start of the validity window, in UTC.</param>
    /// <param name="notAfter">End of the validity window, in UTC.</param>
    /// <param name="isCurrent">Whether the key becomes the signing key.</param>
    /// <returns>The generated material, private half included.</returns>
    /// <exception cref="NotSupportedException">The algorithm is not implemented here.</exception>
    public static SigningKeyMaterial Generate(
        string algorithmId,
        string purpose,
        DateTimeOffset notBefore,
        DateTimeOffset? notAfter,
        bool isCurrent = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(algorithmId);
        ArgumentException.ThrowIfNullOrEmpty(purpose);

        (byte[] privateKey, byte[] publicKey) = GenerateMaterial(algorithmId);

        return new SigningKeyMaterial
        {
            Kid = NewKeyId(algorithmId),
            AlgorithmId = algorithmId,
            PrivateKey = privateKey,
            PublicKey = publicKey,
            Purpose = purpose,
            NotBefore = notBefore,
            NotAfter = notAfter,
            IsCurrent = isCurrent,
        };
    }

    /// <summary>
    /// Builds a signer from key material.
    /// </summary>
    /// <param name="material">The material, which must carry a private key.</param>
    /// <returns>The signer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="material"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The material has no private key.</exception>
    /// <exception cref="NotSupportedException">The algorithm is not implemented here.</exception>
    public static ISigner CreateSigner(SigningKeyMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);

        if (material.PrivateKey.Length == 0)
        {
            throw new ArgumentException(
                "Key material without a private key can verify but cannot sign.",
                nameof(material));
        }

        ReadOnlySpan<byte> privateKey = material.PrivateKey;

        switch (material.AlgorithmId)
        {
            case SignatureAlgorithms.Hs256:
                return new HmacSigner(material.Kid, privateKey);

            case SignatureAlgorithms.Ed25519:
                return new Ed25519Signer(material.Kid, privateKey);

            case SignatureAlgorithms.MlDsa65:
                return new MlDsa65Signer(material.Kid, privateKey);

            case SignatureAlgorithms.SlhDsa128s:
                return new SlhDsa128sSigner(material.Kid, privateKey);

            case SignatureAlgorithms.Ed25519MlDsa65:
                if (!CompositeEncoding.TrySplit(privateKey, out ReadOnlySpan<byte> classical, out ReadOnlySpan<byte> postQuantum))
                {
                    throw new ArgumentException("Composite private key material is malformed.", nameof(material));
                }

                return new CompositeSigner(
                    material.Kid,
                    new Ed25519Signer(material.Kid, classical),
                    new MlDsa65Signer(material.Kid, postQuantum));

            default:
                throw new NotSupportedException(
                    "Signature algorithm " + material.AlgorithmId + " is not implemented by this provider.");
        }
    }

    /// <summary>
    /// Builds a verification key from key material, dropping the private half.
    /// </summary>
    /// <param name="material">The material.</param>
    /// <returns>The verification key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="material"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">The algorithm is not implemented here.</exception>
    public static VerificationKey CreateVerificationKey(SigningKeyMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);

        return new VerificationKey(
            material.Kid,
            material.AlgorithmId,
            material.PublicKey,
            material.NotBefore,
            material.NotAfter);
    }

    /// <summary>
    /// Reads key material out of configuration, deriving the public key when it was not supplied.
    /// </summary>
    /// <param name="options">The configured key.</param>
    /// <returns>The parsed material.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A key field is not valid base64, or the
    /// algorithm is not implemented here.</exception>
    public static SigningKeyMaterial FromOptions(SigningKeyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        byte[] privateKey = DecodeBase64(options.PrivateKey, options.Kid, nameof(SigningKeyOptions.PrivateKey));
        byte[] publicKey = string.IsNullOrEmpty(options.PublicKey)
            ? DerivePublicKey(options.Algorithm, privateKey, options.Kid)
            : DecodeBase64(options.PublicKey, options.Kid, nameof(SigningKeyOptions.PublicKey));

        return new SigningKeyMaterial
        {
            Kid = options.Kid,
            AlgorithmId = options.Algorithm,
            PrivateKey = privateKey,
            PublicKey = publicKey,
            Purpose = options.Purpose,
            NotBefore = options.NotBefore,
            NotAfter = options.NotAfter,
            IsCurrent = options.IsCurrent,
        };
    }

    /// <summary>
    /// Derives the bootstrap signing key for the configured algorithm from the master secret.
    /// </summary>
    /// <param name="options">The crypto options.</param>
    /// <param name="notBefore">Start of the validity window, in UTC.</param>
    /// <returns>The derived material.</returns>
    /// <remarks>
    /// <para>
    /// Derivation rather than generation is what lets a deployment that has configured nothing but
    /// <see cref="CryptoOptions.MasterSecret"/> come up with a working signer: the result is the
    /// same on every instance and across restarts, so two replicas verify each other's signatures
    /// without a shared database and without a rotation dance on every deploy. HKDF gives each
    /// algorithm its own label, so switching the configured algorithm derives a genuinely
    /// different key rather than reusing one across schemes.
    /// </para>
    /// <para>
    /// <see cref="SignatureAlgorithms.SlhDsa128s"/> is the exception. Its private key is a
    /// structured 64 byte encoding whose last half is computed by the key generation procedure, so
    /// random or derived bytes do not form a usable key; an SLH-DSA deployment has to supply a
    /// generated key through configuration or a key store. That is not a real limitation: §E.4.1
    /// puts SLH-DSA on release artefacts (K7), which are signed offline anyway.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The configured algorithm has no derivable key.</exception>
    public static SigningKeyMaterial DeriveBootstrapKey(CryptoOptions options, DateTimeOffset notBefore)
    {
        ArgumentNullException.ThrowIfNull(options);

        string algorithmId = options.SigningAlgorithm;
        (byte[] privateKey, byte[] publicKey) = DeriveMaterial(options, algorithmId);

        byte[] identifier = CryptoKeyDerivation.Derive(
            options.MasterSecret,
            overrideSecret: null,
            CryptoKeyDerivation.SigningKidLabel + ":" + algorithmId,
            KeyIdEntropyBytes);

        return new SigningKeyMaterial
        {
            Kid = AlgorithmSlug(algorithmId) + "-" + Base64Url.EncodeToString(identifier),
            AlgorithmId = algorithmId,
            PrivateKey = privateKey,
            PublicKey = publicKey,
            Purpose = SigningKeyPurposes.Token,
            NotBefore = notBefore,
            NotAfter = null,
            IsCurrent = true,
        };
    }

    private static (byte[] PrivateKey, byte[] PublicKey) DeriveMaterial(CryptoOptions options, string algorithmId)
    {
        switch (algorithmId)
        {
            case SignatureAlgorithms.Hs256:
            {
                byte[] secret = DeriveFor(options, algorithmId, CryptoKeyDerivation.KeyLength);
                return (secret, secret);
            }

            case SignatureAlgorithms.Ed25519:
            {
                byte[] privateKey = DeriveFor(options, algorithmId, Ed25519Signer.PrivateKeySize);
                return (privateKey, new Ed25519Signer("derive", privateKey).GetPublicKey());
            }

            case SignatureAlgorithms.MlDsa65:
            {
                byte[] seed = DeriveFor(options, algorithmId, MlDsa65Signer.SeedSize);
                return (seed, new MlDsa65Signer("derive", seed).GetPublicKey());
            }

            case SignatureAlgorithms.Ed25519MlDsa65:
            {
                byte[] combined = DeriveFor(
                    options,
                    algorithmId,
                    Ed25519Signer.PrivateKeySize + MlDsa65Signer.SeedSize);

                byte[] classicalPrivate = combined[..Ed25519Signer.PrivateKeySize];
                byte[] postQuantumSeed = combined[Ed25519Signer.PrivateKeySize..];

                return (
                    CompositeEncoding.Join(classicalPrivate, postQuantumSeed),
                    CompositeEncoding.Join(
                        new Ed25519Signer("derive", classicalPrivate).GetPublicKey(),
                        new MlDsa65Signer("derive", postQuantumSeed).GetPublicKey()));
            }

            case SignatureAlgorithms.SlhDsa128s:
                throw new InvalidOperationException(
                    "SLHDSA128s keys cannot be derived from the master secret. Configure a generated key under " +
                    "Dle:Crypto:Keys or register a durable ISigningKeyStore that already holds one.");

            default:
                throw new InvalidOperationException(
                    "Signature algorithm " + algorithmId + " is not implemented by this provider.");
        }
    }

    private static byte[] DeriveFor(CryptoOptions options, string algorithmId, int length) =>
        CryptoKeyDerivation.Derive(
            options.MasterSecret,
            overrideSecret: null,
            CryptoKeyDerivation.SigningKeyLabel + ":" + algorithmId,
            length);

    private static (byte[] PrivateKey, byte[] PublicKey) GenerateMaterial(string algorithmId)
    {
        switch (algorithmId)
        {
            case SignatureAlgorithms.Hs256:
            {
                byte[] secret = RandomNumberGenerator.GetBytes(32);
                return (secret, secret);
            }

            case SignatureAlgorithms.Ed25519:
            {
                byte[] privateKey = RandomNumberGenerator.GetBytes(Ed25519Signer.PrivateKeySize);
                return (privateKey, new Ed25519Signer("generate", privateKey).GetPublicKey());
            }

            case SignatureAlgorithms.MlDsa65:
            {
                byte[] seed = RandomNumberGenerator.GetBytes(MlDsa65Signer.SeedSize);
                return (seed, new MlDsa65Signer("generate", seed).GetPublicKey());
            }

            case SignatureAlgorithms.SlhDsa128s:
            {
                SlhDsaKeyPairGenerator generator = new();
                generator.Init(new SlhDsaKeyGenerationParameters(new SecureRandom(), SlhDsaParameters.slh_dsa_sha2_128s));
                Org.BouncyCastle.Crypto.AsymmetricCipherKeyPair pair = generator.GenerateKeyPair();

                SlhDsaPrivateKeyParameters privateKey = (SlhDsaPrivateKeyParameters)pair.Private;
                return (privateKey.GetEncoded(), privateKey.GetPublicKeyEncoded());
            }

            case SignatureAlgorithms.Ed25519MlDsa65:
            {
                byte[] classicalPrivate = RandomNumberGenerator.GetBytes(Ed25519Signer.PrivateKeySize);
                byte[] postQuantumSeed = RandomNumberGenerator.GetBytes(MlDsa65Signer.SeedSize);

                byte[] classicalPublic = new Ed25519Signer("generate", classicalPrivate).GetPublicKey();
                byte[] postQuantumPublic = new MlDsa65Signer("generate", postQuantumSeed).GetPublicKey();

                return (
                    CompositeEncoding.Join(classicalPrivate, postQuantumSeed),
                    CompositeEncoding.Join(classicalPublic, postQuantumPublic));
            }

            default:
                throw new NotSupportedException(
                    "Signature algorithm " + algorithmId + " is not implemented by this provider.");
        }
    }

    private static byte[] DerivePublicKey(string algorithmId, byte[] privateKey, string kid)
    {
        try
        {
            switch (algorithmId)
            {
                case SignatureAlgorithms.Hs256:
                    return privateKey;

                case SignatureAlgorithms.Ed25519:
                    return new Ed25519Signer(kid, privateKey).GetPublicKey();

                case SignatureAlgorithms.MlDsa65:
                    return new MlDsa65Signer(kid, privateKey).GetPublicKey();

                case SignatureAlgorithms.SlhDsa128s:
                    return new SlhDsa128sSigner(kid, privateKey).GetPublicKey();

                case SignatureAlgorithms.Ed25519MlDsa65:
                {
                    if (!CompositeEncoding.TrySplit(privateKey, out ReadOnlySpan<byte> classical, out ReadOnlySpan<byte> postQuantum))
                    {
                        throw new InvalidOperationException(
                            "Signing key " + kid + " has malformed composite private key material.");
                    }

                    return CompositeEncoding.Join(
                        new Ed25519Signer(kid, classical).GetPublicKey(),
                        new MlDsa65Signer(kid, postQuantum).GetPublicKey());
                }

                default:
                    throw new NotSupportedException(
                        "Signature algorithm " + algorithmId + " is not implemented by this provider.");
            }
        }
        catch (ArgumentException e)
        {
            throw new InvalidOperationException(
                "Signing key " + kid + " has private key material that does not match algorithm " + algorithmId + ".",
                e);
        }
    }

    private static byte[] DecodeBase64(string value, string kid, string field)
    {
        byte[] buffer = new byte[((value.Length + 3) / 4) * 3];

        if (!Convert.TryFromBase64String(value, buffer, out int written))
        {
            throw new InvalidOperationException(
                "Signing key " + kid + " has a " + field + " value that is not valid base64.");
        }

        return buffer[..written];
    }

    private static string NewKeyId(string algorithmId) =>
        AlgorithmSlug(algorithmId) + "-" + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(KeyIdEntropyBytes));

    /// <summary>
    /// Lower cased, dot free rendering of an algorithm identifier, so that a key identifier stays
    /// readable in a log and never collides with the segment separator of the token format.
    /// </summary>
    private static string AlgorithmSlug(string algorithmId) => algorithmId switch
    {
        SignatureAlgorithms.Hs256 => "hs256",
        SignatureAlgorithms.Ed25519 => "ed25519",
        SignatureAlgorithms.MlDsa65 => "mldsa65",
        SignatureAlgorithms.Ed25519MlDsa65 => "ed25519-mldsa65",
        SignatureAlgorithms.SlhDsa128s => "slhdsa128s",
        _ => "key",
    };
}
