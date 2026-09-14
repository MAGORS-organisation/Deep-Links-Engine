using System.Text;

using Dle.Crypto;
using Dle.Domain.Crypto;

using Xunit;

namespace Dle.UnitTests.Crypto;

/// <summary>
/// The signer commits to one algorithm; the verifier accepts a set (§E.4.2). That asymmetry is what
/// makes an algorithm migration possible without an outage, and the closed accepted set is what
/// stops the classic downgrade: rewrite the algorithm of a token and hope the verifier follows the
/// token rather than its own policy.
/// </summary>
public sealed class SignatureTests
{
    private static readonly byte[] Payload = "the quick brown fox jumps over the lazy dog"u8.ToArray();

    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> ClassicalAndLatticeAlgorithms() =>
    [
        SignatureAlgorithms.Hs256,
        SignatureAlgorithms.Ed25519,
        SignatureAlgorithms.MlDsa65,
        SignatureAlgorithms.Ed25519MlDsa65,
    ];

    [Theory]
    [MemberData(nameof(ClassicalAndLatticeAlgorithms))]
    public void SignThenVerify_RoundTrips(string algorithmId)
    {
        (ISigner signer, VerificationKey key) = KeyPair(algorithmId);

        byte[] signature = signer.Sign(Payload);

        Assert.Equal(algorithmId, signer.AlgorithmId);
        Assert.True(signature.Length <= signer.MaxSignatureSize);
        Assert.True(key.Verify(Payload, signature));
    }

    [Theory]
    [MemberData(nameof(ClassicalAndLatticeAlgorithms))]
    public void Verify_TamperedPayload_Fails(string algorithmId)
    {
        (ISigner signer, VerificationKey key) = KeyPair(algorithmId);

        byte[] signature = signer.Sign(Payload);

        byte[] tampered = [.. Payload];
        tampered[^1] ^= 0x01;

        Assert.False(key.Verify(tampered, signature));
    }

    [Theory]
    [MemberData(nameof(ClassicalAndLatticeAlgorithms))]
    public void Verify_TamperedSignature_Fails(string algorithmId)
    {
        (ISigner signer, VerificationKey key) = KeyPair(algorithmId);

        byte[] signature = signer.Sign(Payload);
        signature[0] ^= 0x80;

        Assert.False(key.Verify(Payload, signature));
    }

    [Theory]
    [MemberData(nameof(ClassicalAndLatticeAlgorithms))]
    public void Verify_SignatureFromAnotherKeyOfTheSameAlgorithm_Fails(string algorithmId)
    {
        (ISigner signer, _) = KeyPair(algorithmId);
        (_, VerificationKey otherKey) = KeyPair(algorithmId);

        Assert.False(otherKey.Verify(Payload, signer.Sign(Payload)));
    }

    /// <summary>
    /// A composite signature is only valid when both halves are. Accepting a signature whose
    /// post-quantum half is forged would make the hybrid mode security theatre.
    /// </summary>
    [Fact]
    public void Verify_CompositeSignatureWithOneHalfBroken_Fails()
    {
        (ISigner signer, VerificationKey key) = KeyPair(SignatureAlgorithms.Ed25519MlDsa65);

        byte[] signature = signer.Sign(Payload);

        Assert.True(key.Verify(Payload, signature));

        // The classical half sits immediately after the four-byte length prefix; the post-quantum
        // half sits at the end.
        byte[] classicalBroken = [.. signature];
        classicalBroken[5] ^= 0x01;

        byte[] postQuantumBroken = [.. signature];
        postQuantumBroken[^1] ^= 0x01;

        Assert.False(key.Verify(Payload, classicalBroken));
        Assert.False(key.Verify(Payload, postQuantumBroken));
    }

    [Fact]
    public void Verify_HashBasedSignature_RoundTripsAndRejectsTampering()
    {
        // SLH-DSA is deliberately exercised on its own: signing is orders of magnitude slower than
        // the lattice scheme, so it is one case rather than a row in every matrix above.
        (ISigner signer, VerificationKey key) = KeyPair(SignatureAlgorithms.SlhDsa128s);

        byte[] signature = signer.Sign(Payload);

        Assert.True(key.Verify(Payload, signature));

        byte[] tampered = [.. Payload];
        tampered[0] ^= 0x01;

        Assert.False(key.Verify(tampered, signature));
    }

    [Fact]
    public void Verifier_AlgorithmOutsideTheAcceptedSet_IsRefusedEvenWhenTheSignatureIsValid()
    {
        (ISigner signer, VerificationKey key) = KeyPair(SignatureAlgorithms.Ed25519);

        byte[] signature = signer.Sign(Payload);

        // The signature really is valid — the key itself says so.
        Assert.True(key.Verify(Payload, signature));

        var verifier = new MultiAlgorithmVerifier(
            [SignatureAlgorithms.Hs256],
            [key],
            new CryptoTestClock(Now));

        Assert.False(verifier.Verify(SignatureAlgorithms.Ed25519, key.Kid, Payload, signature));
        Assert.DoesNotContain(SignatureAlgorithms.Ed25519, verifier.AcceptedAlgorithms);
    }

    [Fact]
    public void Verifier_AlgorithmThatDoesNotMatchTheKey_IsRefused()
    {
        (ISigner signer, VerificationKey key) = KeyPair(SignatureAlgorithms.Ed25519);

        var verifier = new MultiAlgorithmVerifier(
            [SignatureAlgorithms.Hs256, SignatureAlgorithms.Ed25519],
            [key],
            new CryptoTestClock(Now));

        // Both the algorithm and the key identifier are accepted values, but the key was not issued
        // for that algorithm. This is confusion, not a mismatch, and it has to be refused.
        Assert.False(verifier.Verify(SignatureAlgorithms.Hs256, key.Kid, Payload, signer.Sign(Payload)));
    }

    [Fact]
    public void Verifier_UnknownKeyIdentifier_IsRefused()
    {
        (ISigner signer, VerificationKey key) = KeyPair(SignatureAlgorithms.Ed25519);

        var verifier = new MultiAlgorithmVerifier(
            [SignatureAlgorithms.Ed25519],
            [key],
            new CryptoTestClock(Now));

        Assert.False(verifier.Verify(SignatureAlgorithms.Ed25519, "no-such-kid", Payload, signer.Sign(Payload)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Verifier_BlankAlgorithmOrKey_IsRefused(string? blank)
    {
        (ISigner signer, VerificationKey key) = KeyPair(SignatureAlgorithms.Ed25519);

        var verifier = new MultiAlgorithmVerifier(
            [SignatureAlgorithms.Ed25519],
            [key],
            new CryptoTestClock(Now));

        byte[] signature = signer.Sign(Payload);

        Assert.False(verifier.Verify(blank!, key.Kid, Payload, signature));
        Assert.False(verifier.Verify(SignatureAlgorithms.Ed25519, blank!, Payload, signature));
    }

    [Fact]
    public void Verifier_KeyOutsideItsValidityWindow_IsRefused()
    {
        SigningKeyMaterial material = SigningKeyFactory.Generate(
            SignatureAlgorithms.Ed25519,
            SigningKeyPurposes.Token,
            notBefore: Now,
            notAfter: Now.AddDays(1));

        ISigner signer = SigningKeyFactory.CreateSigner(material);
        VerificationKey key = SigningKeyFactory.CreateVerificationKey(material);

        byte[] signature = signer.Sign(Payload);

        var clock = new CryptoTestClock(Now.AddHours(1));
        var verifier = new MultiAlgorithmVerifier([SignatureAlgorithms.Ed25519], [key], clock);

        Assert.True(verifier.Verify(SignatureAlgorithms.Ed25519, key.Kid, Payload, signature));

        clock.Now = Now.AddDays(2);
        Assert.False(verifier.Verify(SignatureAlgorithms.Ed25519, key.Kid, Payload, signature));

        clock.Now = Now.AddDays(-1);
        Assert.False(verifier.Verify(SignatureAlgorithms.Ed25519, key.Kid, Payload, signature));
    }

    [Fact]
    public void Verifier_DuplicateKeyIdentifier_KeepsTheFirstKey()
    {
        SigningKeyMaterial first = Material(SignatureAlgorithms.Ed25519);
        SigningKeyMaterial second = Material(SignatureAlgorithms.Ed25519);

        // A second row carrying an identifier that is already in use — a stale or hostile row. The
        // key already in service must win, so that it cannot be displaced at verification time.
        SigningKeyMaterial impostor = new()
        {
            Kid = first.Kid,
            AlgorithmId = SignatureAlgorithms.Ed25519,
            PublicKey = second.PublicKey,
            PrivateKey = second.PrivateKey,
            NotBefore = second.NotBefore,
            NotAfter = second.NotAfter,
        };

        var verifier = new MultiAlgorithmVerifier(
            [SignatureAlgorithms.Ed25519],
            [SigningKeyFactory.CreateVerificationKey(first), SigningKeyFactory.CreateVerificationKey(impostor)],
            new CryptoTestClock(Now));

        Assert.NotEqual(first.Kid, second.Kid);

        Assert.True(verifier.Verify(
            SignatureAlgorithms.Ed25519, first.Kid, Payload, SigningKeyFactory.CreateSigner(first).Sign(Payload)));

        Assert.False(verifier.Verify(
            SignatureAlgorithms.Ed25519, first.Kid, Payload, SigningKeyFactory.CreateSigner(second).Sign(Payload)));
    }

    [Fact]
    public void HmacSigner_KeyShorterThanTheDigest_Throws() =>
        Assert.Throws<ArgumentException>(() => new HmacSigner("kid", new byte[31]));

    [Fact]
    public void Ed25519Signer_PrivateKeyOfTheWrongLength_Throws() =>
        Assert.Throws<ArgumentException>(() => new Ed25519Signer("kid", new byte[31]));

    [Fact]
    public void HmacVerificationKey_HasNoJsonWebKey()
    {
        (_, VerificationKey key) = KeyPair(SignatureAlgorithms.Hs256);

        // Publishing a symmetric key through JWKS would publish the signing secret itself.
        Assert.Null(key.ToJsonWebKey());
    }

    [Fact]
    public void Ed25519VerificationKey_PublishesAnOkpJsonWebKey()
    {
        (_, VerificationKey key) = KeyPair(SignatureAlgorithms.Ed25519);

        JsonWebKey? jwk = key.ToJsonWebKey();

        Assert.NotNull(jwk);
        Assert.Equal("OKP", jwk.Kty);
        Assert.Equal("Ed25519", jwk.Crv);
        Assert.Equal("sig", jwk.Use);
        Assert.Equal(key.Kid, jwk.Kid);
        Assert.False(string.IsNullOrEmpty(jwk.X));
    }

    [Fact]
    public void HmacVerification_UsesFixedTimeEquals() =>
        Assert.True(
            ConstantTimeAssertion.CallsFixedTimeEquals(typeof(VerificationKey), "VerifyHmac"),
            "VerificationKey.VerifyHmac must compare digests with CryptographicOperations.FixedTimeEquals.");

    private static SigningKeyMaterial Material(string algorithmId) => SigningKeyFactory.Generate(
        algorithmId,
        SigningKeyPurposes.Token,
        notBefore: Now.AddDays(-1),
        notAfter: Now.AddDays(30));

    private static (ISigner Signer, VerificationKey Key) KeyPair(string algorithmId)
    {
        SigningKeyMaterial material = Material(algorithmId);

        return (SigningKeyFactory.CreateSigner(material), SigningKeyFactory.CreateVerificationKey(material));
    }

    /// <summary>Guards against an accidental change to the payload literal above.</summary>
    [Fact]
    public void Payload_IsTheExpectedBytes() =>
        Assert.Equal("the quick brown fox jumps over the lazy dog", Encoding.UTF8.GetString(Payload));
}
