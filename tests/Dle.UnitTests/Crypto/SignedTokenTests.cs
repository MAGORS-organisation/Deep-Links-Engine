using Dle.Crypto;
using Dle.Domain.Crypto;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Xunit;

namespace Dle.UnitTests.Crypto;

/// <summary>
/// The token format is <c>dlt1.&lt;alg&gt;.&lt;kid&gt;.&lt;payload&gt;.&lt;signature&gt;</c> and the
/// signature covers the first four segments, not only the payload (§E.4.2). Signing the payload
/// alone would leave the algorithm and the key identifier unauthenticated — the classic JWT
/// confusion failure.
/// </summary>
public sealed class SignedTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 4, 2, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void ToString_ProducesFiveDottedSegmentsWithTheVersionPrefix()
    {
        var token = new SignedToken("Ed25519", "kid-1", "cGF5bG9hZA", "c2ln");

        string wire = token.ToString();

        Assert.Equal("dlt1.Ed25519.kid-1.cGF5bG9hZA.c2ln", wire);
        Assert.Equal(5, wire.Split('.').Length);
    }

    [Fact]
    public void TryParse_WellFormedToken_RoundTrips()
    {
        var original = new SignedToken("HS256", "kid-1", "cGF5bG9hZA", "c2ln");

        Assert.True(SignedToken.TryParse(original.ToString(), out SignedToken parsed));
        Assert.Equal(original, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dlt1.HS256.kid.payload")]
    [InlineData("dlt1.HS256.kid.payload.sig.extra")]
    [InlineData("dlt2.HS256.kid.cGF5bG9hZA.c2ln")]
    [InlineData("dlt1..kid.cGF5bG9hZA.c2ln")]
    [InlineData("dlt1.HS256..cGF5bG9hZA.c2ln")]
    [InlineData("dlt1.HS256.kid..c2ln")]
    [InlineData("dlt1.HS256.kid.cGF5bG9hZA.")]
    [InlineData("dlt1.HS256.kid.pay+load.c2ln")]
    [InlineData("dlt1.HS256.kid.payload/x.c2ln")]
    [InlineData("dlt1.HS256.kid.cGF5bG9hZA.c2l=")]
    [InlineData("dlt1.HS256.kid.a.c2ln")]
    public void TryParse_MalformedToken_IsRefused(string? candidate)
    {
        Assert.False(SignedToken.TryParse(candidate, out SignedToken parsed));
        Assert.Equal(string.Empty, parsed.Alg);
        Assert.Equal(string.Empty, parsed.Kid);
    }

    [Fact]
    public void Validate_TokenIssuedByTheCodec_IsAccepted()
    {
        SignedTokenCodec codec = Codec(out _, Now);

        string token = codec.Issue(claims: new Dictionary<string, string>(StringComparer.Ordinal) { ["sub"] = "tenant-1" });

        TokenValidationResult result = codec.Validate(token);

        Assert.True(result.IsValid);
        Assert.Equal(TokenValidationResult.ReasonValid, result.Reason);
        Assert.NotNull(result.Payload);
        Assert.Equal("dle", result.Payload.Aud);
        Assert.Equal("tenant-1", result.Payload.Claims!["sub"]);
    }

    [Fact]
    public void Validate_TokenWithARewrittenAlgorithmSegment_FailsTheSignature()
    {
        SignedTokenCodec codec = Codec(out _, Now);

        Assert.True(SignedToken.TryParse(codec.Issue(), out SignedToken issued));

        // The header is inside the signed input, so renaming the algorithm breaks the signature
        // rather than selecting a different verification path.
        string downgraded = new SignedToken("HS256", issued.Kid, issued.Payload, issued.Signature).ToString();

        Assert.Equal(TokenValidationResult.ReasonSignature, codec.Validate(downgraded).Reason);
    }

    [Fact]
    public void Validate_TokenWithATamperedPayload_FailsTheSignature()
    {
        SignedTokenCodec codec = Codec(out _, Now);

        Assert.True(SignedToken.TryParse(codec.Issue(), out SignedToken issued));

        char[] payload = issued.Payload.ToCharArray();
        payload[0] = payload[0] == 'A' ? 'B' : 'A';

        string tampered = new SignedToken(issued.Alg, issued.Kid, new string(payload), issued.Signature).ToString();

        Assert.Equal(TokenValidationResult.ReasonSignature, codec.Validate(tampered).Reason);
    }

    [Fact]
    public void Validate_ExpiredToken_IsRefused()
    {
        SignedTokenCodec codec = Codec(out CryptoTestClock clock, Now);

        string token = codec.Issue(lifetime: TimeSpan.FromSeconds(60));

        clock.Now = Now.AddSeconds(59);
        Assert.True(codec.Validate(token).IsValid);

        // 60 seconds of lifetime plus the 60 second default skew.
        clock.Now = Now.AddSeconds(121);
        Assert.Equal(TokenValidationResult.ReasonExpired, codec.Validate(token).Reason);
    }

    [Fact]
    public void Validate_TokenForAnotherAudience_IsRefused()
    {
        SignedTokenCodec codec = Codec(out _, Now);

        string token = codec.Issue(audience: "dle-admin");

        Assert.True(codec.Validate(token, "dle-admin").IsValid);
        Assert.Equal(TokenValidationResult.ReasonAudience, codec.Validate(token, "dle-edge").Reason);
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("dlt1.HS256.kid.payload")]
    [InlineData("")]
    public void Validate_MalformedToken_IsRefusedWithoutThrowing(string candidate) =>
        Assert.Equal(TokenValidationResult.ReasonMalformed, Codec(out _, Now).Validate(candidate).Reason);

    [Fact]
    public async Task ValidateOnceAsync_SameTokenTwice_IsRefusedTheSecondTime()
    {
        SignedTokenCodec codec = Codec(out _, Now);

        string token = codec.Issue();

        TokenValidationResult first = await codec.ValidateOnceAsync(token, null, TestContext.Current.CancellationToken);
        TokenValidationResult second = await codec.ValidateOnceAsync(token, null, TestContext.Current.CancellationToken);

        Assert.True(first.IsValid);
        Assert.False(second.IsValid);
        Assert.Equal(TokenValidationResult.ReasonReplayed, second.Reason);
    }

    private static SignedTokenCodec Codec(out CryptoTestClock clock, DateTimeOffset now)
    {
        clock = new CryptoTestClock(now);

        var options = new CryptoOptions
        {
            MasterSecret = CryptoTestKeys.MasterSecret,
            SigningAlgorithm = SignatureAlgorithms.Ed25519,
        };

        SigningKeyMaterial material = SigningKeyFactory.Generate(
            SignatureAlgorithms.Ed25519,
            SigningKeyPurposes.Token,
            notBefore: now.AddDays(-1),
            notAfter: now.AddDays(30));

        var keyRing = new StaticKeyRing(material, clock);

        return new SignedTokenCodec(
            keyRing,
            new InMemoryTokenReplayGuard(options.ReplayGuardCapacity, clock, NullLogger<InMemoryTokenReplayGuard>.Instance),
            Options.Create(options),
            clock);
    }

    /// <summary>A key ring with one key, so the codec can be exercised without a key store.</summary>
    private sealed class StaticKeyRing : IKeyRing
    {
        private readonly SigningKeyMaterial _material;

        internal StaticKeyRing(SigningKeyMaterial material, TimeProvider timeProvider)
        {
            _material = material;
            CurrentSigner = SigningKeyFactory.CreateSigner(material);
            Verifier = new MultiAlgorithmVerifier(
                [material.AlgorithmId],
                [SigningKeyFactory.CreateVerificationKey(material)],
                timeProvider);
        }

        public ISigner CurrentSigner { get; }

        public IVerifier Verifier { get; }

        public JwksDocument GetJwks()
        {
            JsonWebKey? key = SigningKeyFactory.CreateVerificationKey(_material).ToJsonWebKey();

            return new JwksDocument(key is null ? [] : [key]);
        }

        public ValueTask RotateAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }
}
