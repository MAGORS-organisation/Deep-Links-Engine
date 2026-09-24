using Dle.Crypto;
using Dle.Domain.Crypto;

using Xunit;

namespace Dle.UnitTests.Crypto;

/// <summary>
/// The algorithm provider behind the key ring (§E.4.2, §E.5.2, NFR-17).
/// </summary>
/// <remarks>
/// <para>
/// Crypto agility means one thing operationally: every algorithm in the published set can be
/// generated, signed with, verified against and published, and switching between them is a
/// configuration change rather than a code change. That is what these tests assert, algorithm by
/// algorithm, rather than testing only the default.
/// </para>
/// <para>
/// SLH-DSA is the documented exception to derivation: its private key is a structured encoding
/// whose second half is produced by key generation, so derived bytes do not form a usable key and
/// the factory has to say so rather than produce something that fails later.
/// </para>
/// </remarks>
public sealed class SigningKeyFactoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Every algorithm the provider claims to implement.</summary>
    public static TheoryData<string> AllAlgorithms =>
    [
        SignatureAlgorithms.Hs256,
        SignatureAlgorithms.Ed25519,
        SignatureAlgorithms.MlDsa65,
        SignatureAlgorithms.Ed25519MlDsa65,
        SignatureAlgorithms.SlhDsa128s,
    ];

    /// <summary>The algorithms whose key material can be derived from the master secret.</summary>
    public static TheoryData<string> DerivableAlgorithms =>
    [
        SignatureAlgorithms.Hs256,
        SignatureAlgorithms.Ed25519,
        SignatureAlgorithms.MlDsa65,
        SignatureAlgorithms.Ed25519MlDsa65,
    ];

    // ------------------------------------------------------------------ the published set

    [Fact]
    public void SupportedAlgorithms_AreExactlyTheSetE42Publishes()
    {
        // §E.4.2 fixes the set. Adding one is a contract change; dropping one breaks every token
        // already in circulation that carries it in its alg segment.
        string[] expected =
        [
            SignatureAlgorithms.Ed25519,
            SignatureAlgorithms.Ed25519MlDsa65,
            SignatureAlgorithms.Hs256,
            SignatureAlgorithms.MlDsa65,
            SignatureAlgorithms.SlhDsa128s,
        ];

        List<string> actual = [.. SigningKeyFactory.SupportedAlgorithms.Order(StringComparer.Ordinal)];

        Assert.Equal(expected.Length, actual.Count);
        Assert.All(expected, algorithm => Assert.Contains(algorithm, actual));
    }

    [Fact]
    public void PostQuantumAlgorithms_AreASubsetOfTheSupportedSet()
    {
        Assert.All(
            SigningKeyFactory.PostQuantumAlgorithms,
            algorithm => Assert.Contains(algorithm, SigningKeyFactory.SupportedAlgorithms));
    }

    [Theory]
    [MemberData(nameof(AllAlgorithms))]
    public void IsSupported_APublishedAlgorithm_IsTrue(string algorithm)
    {
        Assert.True(SigningKeyFactory.IsSupported(algorithm));
    }

    [Theory]
    [InlineData("RS256")]
    [InlineData("none")]
    [InlineData("")]
    [InlineData(null)]
    public void IsSupported_AnythingElse_IsFalse(string? algorithm)
    {
        // "none" in particular: the classic JWT downgrade is refused before a key is even looked up.
        Assert.False(SigningKeyFactory.IsSupported(algorithm));
    }

    [Theory]
    [InlineData(SignatureAlgorithms.MlDsa65, true)]
    [InlineData(SignatureAlgorithms.Ed25519MlDsa65, true)]
    [InlineData(SignatureAlgorithms.SlhDsa128s, true)]
    [InlineData(SignatureAlgorithms.Hs256, false)]
    [InlineData(SignatureAlgorithms.Ed25519, false)]
    [InlineData(null, false)]
    public void IsPostQuantum_MatchesTheE53Phasing(string? algorithm, bool expected)
    {
        Assert.Equal(expected, SigningKeyFactory.IsPostQuantum(algorithm));
    }

    // ------------------------------------------------------------------ generate, sign, verify

    [Theory]
    [MemberData(nameof(AllAlgorithms))]
    public void Generate_ThenSignAndVerify_RoundTripsForEveryAlgorithm(string algorithm)
    {
        SigningKeyMaterial material = SigningKeyFactory.Generate(
            algorithm,
            SigningKeyPurposes.Token,
            notBefore: Now,
            notAfter: Now.AddDays(30));

        Assert.Equal(algorithm, material.AlgorithmId);
        Assert.NotEmpty(material.PrivateKey);
        Assert.NotEmpty(material.PublicKey);
        Assert.True(material.IsCurrent);

        ISigner signer = SigningKeyFactory.CreateSigner(material);
        VerificationKey verification = SigningKeyFactory.CreateVerificationKey(material);

        byte[] payload = "agility"u8.ToArray();
        byte[] signature = signer.Sign(payload);

        Assert.True(verification.Verify(payload, signature));
        Assert.False(verification.Verify("tampered"u8.ToArray(), signature));
    }

    [Theory]
    [MemberData(nameof(AllAlgorithms))]
    public void Generate_TwiceForOneAlgorithm_ProducesDistinctKeys(string algorithm)
    {
        SigningKeyMaterial first = SigningKeyFactory.Generate(algorithm, SigningKeyPurposes.Token, Now, null);
        SigningKeyMaterial second = SigningKeyFactory.Generate(algorithm, SigningKeyPurposes.Token, Now, null);

        Assert.NotEqual(first.Kid, second.Kid);
        Assert.NotEqual(first.PublicKey, second.PublicKey);
    }

    [Theory]
    [MemberData(nameof(AllAlgorithms))]
    public void Generate_KeyIdentifier_NeverContainsADot(string algorithm)
    {
        // The token format is dlt1.<alg>.<kid>.<payload>.<signature>; a dot in the kid would make
        // the segments ambiguous.
        SigningKeyMaterial material = SigningKeyFactory.Generate(algorithm, SigningKeyPurposes.Token, Now, null);

        Assert.DoesNotContain('.', material.Kid);
    }

    [Fact]
    public void Generate_UnknownAlgorithm_Throws()
    {
        Assert.Throws<NotSupportedException>(
            () => SigningKeyFactory.Generate("RS256", SigningKeyPurposes.Token, Now, null));
    }

    [Fact]
    public void Generate_EmptyAlgorithmOrPurpose_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => SigningKeyFactory.Generate("", SigningKeyPurposes.Token, Now, null));

        Assert.Throws<ArgumentException>(
            () => SigningKeyFactory.Generate(SignatureAlgorithms.Hs256, "", Now, null));
    }

    // ------------------------------------------------------------------ signer construction

    [Fact]
    public void CreateSigner_MaterialWithoutAPrivateKey_Throws()
    {
        // T-15: the edge verifies and never signs, so it holds material with no private half.
        SigningKeyMaterial verifyOnly = new()
        {
            Kid = "verify-only",
            AlgorithmId = SignatureAlgorithms.Hs256,
            PublicKey = CryptoTestKeys.Primary,
            PrivateKey = [],
        };

        Assert.Throws<ArgumentException>(() => SigningKeyFactory.CreateSigner(verifyOnly));
    }

    [Fact]
    public void CreateSigner_UnknownAlgorithm_Throws()
    {
        SigningKeyMaterial material = new()
        {
            Kid = "k",
            AlgorithmId = "RS256",
            PublicKey = CryptoTestKeys.Primary,
            PrivateKey = CryptoTestKeys.Primary,
        };

        Assert.Throws<NotSupportedException>(() => SigningKeyFactory.CreateSigner(material));
    }

    [Fact]
    public void CreateSigner_MalformedCompositePrivateKey_Throws()
    {
        SigningKeyMaterial material = new()
        {
            Kid = "k",
            AlgorithmId = SignatureAlgorithms.Ed25519MlDsa65,
            PublicKey = [1, 2, 3],
            PrivateKey = [1, 2, 3],
        };

        Assert.Throws<ArgumentException>(() => SigningKeyFactory.CreateSigner(material));
    }

    [Fact]
    public void CreateSigner_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SigningKeyFactory.CreateSigner(null!));
        Assert.Throws<ArgumentNullException>(() => SigningKeyFactory.CreateVerificationKey(null!));
    }

    // ------------------------------------------------------------------ derivation

    [Theory]
    [MemberData(nameof(DerivableAlgorithms))]
    public void DeriveBootstrapKey_IsDeterministicAndUsable(string algorithm)
    {
        CryptoOptions options = new()
        {
            MasterSecret = CryptoTestKeys.MasterSecret,
            SigningAlgorithm = algorithm,
        };

        SigningKeyMaterial first = SigningKeyFactory.DeriveBootstrapKey(options, Now);
        SigningKeyMaterial second = SigningKeyFactory.DeriveBootstrapKey(options, Now);

        Assert.Equal(first.Kid, second.Kid);
        Assert.Equal(first.PrivateKey, second.PrivateKey);
        Assert.Equal(first.PublicKey, second.PublicKey);

        byte[] payload = "derived"u8.ToArray();

        Assert.True(SigningKeyFactory.CreateVerificationKey(first)
            .Verify(payload, SigningKeyFactory.CreateSigner(second).Sign(payload)));
    }

    [Theory]
    [MemberData(nameof(DerivableAlgorithms))]
    public void DeriveBootstrapKey_DifferentAlgorithms_DeriveDifferentKeys(string algorithm)
    {
        // Each algorithm gets its own HKDF label, so switching the configured algorithm must not
        // reuse one scheme's key material under another.
        CryptoOptions baseline = new()
        {
            MasterSecret = CryptoTestKeys.MasterSecret,
            SigningAlgorithm = SignatureAlgorithms.Hs256,
        };

        CryptoOptions other = new()
        {
            MasterSecret = CryptoTestKeys.MasterSecret,
            SigningAlgorithm = algorithm,
        };

        SigningKeyMaterial derivedBaseline = SigningKeyFactory.DeriveBootstrapKey(baseline, Now);
        SigningKeyMaterial derivedOther = SigningKeyFactory.DeriveBootstrapKey(other, Now);

        if (!string.Equals(algorithm, SignatureAlgorithms.Hs256, StringComparison.Ordinal))
        {
            Assert.NotEqual(derivedBaseline.Kid, derivedOther.Kid);
            Assert.NotEqual(derivedBaseline.PrivateKey, derivedOther.PrivateKey);
        }
    }

    [Fact]
    public void DeriveBootstrapKey_ChangingTheMasterSecret_ChangesTheKey()
    {
        CryptoOptions first = new() { MasterSecret = CryptoTestKeys.MasterSecret };
        CryptoOptions second = new() { MasterSecret = CryptoTestKeys.MasterSecret + "-rotated" };

        Assert.NotEqual(
            SigningKeyFactory.DeriveBootstrapKey(first, Now).PrivateKey,
            SigningKeyFactory.DeriveBootstrapKey(second, Now).PrivateKey);
    }

    [Fact]
    public void DeriveBootstrapKey_SlhDsa_IsRefusedWithAnActionableMessage()
    {
        CryptoOptions options = new()
        {
            MasterSecret = CryptoTestKeys.MasterSecret,
            SigningAlgorithm = SignatureAlgorithms.SlhDsa128s,
            HybridPqEnabled = true,
        };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => SigningKeyFactory.DeriveBootstrapKey(options, Now));

        Assert.Contains("Dle:Crypto:Keys", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeriveBootstrapKey_UnknownAlgorithm_IsRefused()
    {
        CryptoOptions options = new()
        {
            MasterSecret = CryptoTestKeys.MasterSecret,
            SigningAlgorithm = "RS256",
        };

        Assert.Throws<InvalidOperationException>(() => SigningKeyFactory.DeriveBootstrapKey(options, Now));
    }

    [Fact]
    public void DeriveBootstrapKey_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SigningKeyFactory.DeriveBootstrapKey(null!, Now));
    }

    // ------------------------------------------------------------------ reading configuration

    [Theory]
    [MemberData(nameof(DerivableAlgorithms))]
    public void FromOptions_PublicKeyOmitted_IsDerivedFromThePrivateHalf(string algorithm)
    {
        SigningKeyMaterial generated = SigningKeyFactory.Generate(algorithm, SigningKeyPurposes.Token, Now, null);

        SigningKeyOptions configured = new()
        {
            Kid = "configured",
            Algorithm = algorithm,
            PrivateKey = Convert.ToBase64String(generated.PrivateKey),
            Purpose = SigningKeyPurposes.Token,
        };

        SigningKeyMaterial material = SigningKeyFactory.FromOptions(configured);

        Assert.Equal(generated.PublicKey, material.PublicKey);
    }

    [Fact]
    public void FromOptions_PublicKeySupplied_IsUsedVerbatim()
    {
        SigningKeyMaterial generated = SigningKeyFactory.Generate(
            SignatureAlgorithms.Ed25519, SigningKeyPurposes.Token, Now, null);

        SigningKeyOptions configured = new()
        {
            Kid = "configured",
            Algorithm = SignatureAlgorithms.Ed25519,
            PrivateKey = Convert.ToBase64String(generated.PrivateKey),
            PublicKey = Convert.ToBase64String(generated.PublicKey),
            Purpose = SigningKeyPurposes.Token,
            NotBefore = Now,
            NotAfter = Now.AddDays(10),
            IsCurrent = true,
        };

        SigningKeyMaterial material = SigningKeyFactory.FromOptions(configured);

        Assert.Equal(generated.PublicKey, material.PublicKey);
        Assert.Equal(Now, material.NotBefore);
        Assert.Equal(Now.AddDays(10), material.NotAfter);
        Assert.True(material.IsCurrent);
    }

    [Fact]
    public void FromOptions_PrivateKeyThatIsNotBase64_NamesTheKeyAndTheField()
    {
        SigningKeyOptions configured = new()
        {
            Kid = "broken",
            Algorithm = SignatureAlgorithms.Hs256,
            PrivateKey = "%%% not base64 %%%",
            Purpose = SigningKeyPurposes.Token,
        };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => SigningKeyFactory.FromOptions(configured));

        Assert.Contains("broken", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromOptions_UnknownAlgorithm_IsRefused()
    {
        SigningKeyOptions configured = new()
        {
            Kid = "k",
            Algorithm = "RS256",
            PrivateKey = Convert.ToBase64String(CryptoTestKeys.Primary),
            Purpose = SigningKeyPurposes.Token,
        };

        // NotSupportedException, the same answer Generate and CreateSigner give to the same
        // condition. FromOptions used to document InvalidOperationException here, which was a
        // documentation error rather than a behavioural one.
        Assert.Throws<NotSupportedException>(() => SigningKeyFactory.FromOptions(configured));
    }

    [Fact]
    public void FromOptions_MalformedCompositePrivateKey_IsRefused()
    {
        SigningKeyOptions configured = new()
        {
            Kid = "k",
            Algorithm = SignatureAlgorithms.Ed25519MlDsa65,
            PrivateKey = Convert.ToBase64String([1, 2, 3]),
            Purpose = SigningKeyPurposes.Token,
        };

        Assert.Throws<InvalidOperationException>(() => SigningKeyFactory.FromOptions(configured));
    }

    [Fact]
    public void FromOptions_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SigningKeyFactory.FromOptions(null!));
    }

    // ------------------------------------------------------------------ publication

    [Fact]
    public void CreateVerificationKey_SymmetricKey_IsNotPublishableAsAJwk()
    {
        // T-15 again, from the other side: an HS256 "public" key is the signing secret.
        SigningKeyMaterial material = SigningKeyFactory.Generate(
            SignatureAlgorithms.Hs256, SigningKeyPurposes.Token, Now, null);

        Assert.Null(SigningKeyFactory.CreateVerificationKey(material).ToJsonWebKey());
    }

    [Theory]
    [InlineData(SignatureAlgorithms.Ed25519)]
    [InlineData(SignatureAlgorithms.MlDsa65)]
    public void CreateVerificationKey_AsymmetricKey_IsPublishableAsAJwk(string algorithm)
    {
        SigningKeyMaterial material = SigningKeyFactory.Generate(
            algorithm, SigningKeyPurposes.Token, Now, null);

        JsonWebKey? jwk = SigningKeyFactory.CreateVerificationKey(material).ToJsonWebKey();

        Assert.NotNull(jwk);
        Assert.Equal(material.Kid, jwk.Kid);
    }
}
