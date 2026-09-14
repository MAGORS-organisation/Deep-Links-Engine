using System.Text;

using Dle.Crypto;

using Microsoft.Extensions.Options;

using Xunit;

namespace Dle.UnitTests.Crypto;

/// <summary>
/// An API key is stored as an Argon2id hash and compared in fixed time (§E.4, S-03). The timing
/// property is asserted structurally rather than by measurement: a timing assertion on a shared CI
/// runner is flaky, and a green flaky test proves nothing.
/// </summary>
public sealed class ApiKeyHashingTests
{
    /// <summary>
    /// Deliberately cheap parameters. The production defaults (19 MiB, two passes) are validated by
    /// the options validator; repeating them in every unit test would buy nothing but seconds.
    /// </summary>
    private static readonly Argon2Options FastArgon2 = new()
    {
        MemoryKib = 8 * 1024,
        Iterations = 1,
        Parallelism = 1,
        HashLength = 32,
        SaltLength = 16,
    };

    private static ApiKeyHasher Hasher(string prefix = "dle")
    {
        var options = new CryptoOptions
        {
            MasterSecret = CryptoTestKeys.MasterSecret,
            ApiKeyPrefix = prefix,
            Argon2 = FastArgon2,
        };

        return new ApiKeyHasher(new Argon2PasswordHasher(FastArgon2), Options.Create(options));
    }

    [Fact]
    public void Create_ProducesAThreeFieldTokenWithTheConfiguredPrefix()
    {
        ApiKeyCredential credential = Hasher("dle").Create();

        string[] fields = credential.Token.Split(ApiKeyHasher.FieldSeparator);

        Assert.Equal(3, fields.Length);
        Assert.Equal("dle", fields[0]);
        Assert.Equal(ApiKeyHasher.PrefixLength, fields[1].Length);
        Assert.Equal(credential.Prefix, fields[1]);
        Assert.NotEmpty(credential.Hash);
    }

    [Fact]
    public void Create_TwoKeys_ShareNothing()
    {
        ApiKeyHasher hasher = Hasher();

        ApiKeyCredential first = hasher.Create();
        ApiKeyCredential second = hasher.Create();

        Assert.NotEqual(first.Token, second.Token);
        Assert.NotEqual(first.Prefix, second.Prefix);
        Assert.NotEqual(first.Hash, second.Hash);
    }

    [Fact]
    public void Verify_TheKeyThatWasIssued_Succeeds()
    {
        ApiKeyCredential credential = Hasher().Create();

        Assert.True(ApiKeyHasher.Verify(credential.Token, credential.Hash));
    }

    [Fact]
    public void Verify_AnotherKey_Fails()
    {
        ApiKeyHasher hasher = Hasher();

        ApiKeyCredential credential = hasher.Create();
        ApiKeyCredential other = hasher.Create();

        Assert.False(ApiKeyHasher.Verify(other.Token, credential.Hash));
    }

    [Fact]
    public void Verify_KeyWithOneCharacterChanged_Fails()
    {
        ApiKeyCredential credential = Hasher().Create();

        char[] mutated = credential.Token.ToCharArray();
        mutated[^1] = mutated[^1] == 'a' ? 'b' : 'a';

        Assert.False(ApiKeyHasher.Verify(new string(mutated), credential.Hash));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Verify_NoKey_Fails(string? presented) =>
        Assert.False(ApiKeyHasher.Verify(presented, Hasher().Create().Hash));

    [Fact]
    public void Verify_NoStoredHash_Fails() =>
        Assert.False(ApiKeyHasher.Verify(Hasher().Create().Token, ReadOnlySpan<byte>.Empty));

    [Fact]
    public void Verify_StoredHashThatIsNotAnArgon2Encoding_Fails() =>
        Assert.False(ApiKeyHasher.Verify("dle_abcdefgh_secret", "not-a-hash"u8.ToArray()));

    [Fact]
    public void TryReadPrefix_WellFormedKey_ReturnsThePrefix()
    {
        ApiKeyCredential credential = Hasher().Create();

        Assert.True(ApiKeyHasher.TryReadPrefix(credential.Token, out string prefix));
        Assert.Equal(credential.Prefix, prefix);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dle_short_secret")]
    [InlineData("dle_abcdefgh")]
    [InlineData("dle_abcdefgh_secret_extra")]
    [InlineData("dle_abcdef!h_0123456789012345678901234567890123456789012")]
    public void TryReadPrefix_MalformedKey_ReturnsFalse(string? candidate)
    {
        Assert.False(ApiKeyHasher.TryReadPrefix(candidate, out string prefix));
        Assert.Equal(string.Empty, prefix);
    }

    [Fact]
    public void Argon2_Hash_IsSaltedSoTwoHashesOfOneSecretDiffer()
    {
        var hasher = new Argon2PasswordHasher(FastArgon2);

        string first = hasher.Hash("correct horse battery staple");
        string second = hasher.Hash("correct horse battery staple");

        Assert.NotEqual(first, second);
        Assert.True(Argon2PasswordHasher.Verify("correct horse battery staple", first));
        Assert.True(Argon2PasswordHasher.Verify("correct horse battery staple", second));
    }

    [Fact]
    public void Argon2_Encoding_NamesArgon2idAndItsCost()
    {
        string encoded = new Argon2PasswordHasher(FastArgon2).Hash("secret");

        Assert.StartsWith("$argon2id$v=19$", encoded, StringComparison.Ordinal);
        Assert.Contains("m=8192,t=1,p=1", encoded, StringComparison.Ordinal);
        Assert.Equal(6, encoded.Split('$').Length);
    }

    [Theory]
    [InlineData(null, "irrelevant")]
    [InlineData("", "irrelevant")]
    [InlineData("secret", null)]
    [InlineData("secret", "")]
    [InlineData("secret", "$argon2i$v=19$m=8192,t=1,p=1$c2FsdA$aGFzaA")]
    [InlineData("secret", "$argon2id$v=19$nonsense$c2FsdA$aGFzaA")]
    [InlineData("secret", "$argon2id$v=19$m=8192,t=1,p=1$not base64!$aGFzaA")]
    public void Argon2_Verify_MalformedInput_ReturnsFalseWithoutThrowing(string? secret, string? encoded) =>
        Assert.False(Argon2PasswordHasher.Verify(secret, encoded));

    [Fact]
    public void Argon2_NeedsRehash_TrueWhenTheStoredCostIsBelowTheConfiguredOne()
    {
        var weak = new Argon2PasswordHasher(FastArgon2);

        var strong = new Argon2PasswordHasher(new Argon2Options
        {
            MemoryKib = 16 * 1024,
            Iterations = 2,
            Parallelism = 1,
        });

        string stored = weak.Hash("secret");

        Assert.False(weak.NeedsRehash(stored));
        Assert.True(strong.NeedsRehash(stored));
        Assert.True(strong.NeedsRehash(null));
    }

    /// <summary>
    /// S-03. The comparison of a derived hash with the stored one must not exit early: an early
    /// exit turns key verification into a byte-at-a-time search.
    /// </summary>
    [Fact]
    public void Argon2_Verification_UsesFixedTimeEquals() =>
        Assert.True(
            ConstantTimeAssertion.CallsFixedTimeEquals(typeof(Argon2PasswordHasher), nameof(Argon2PasswordHasher.Verify)),
            "Argon2PasswordHasher.Verify must compare with CryptographicOperations.FixedTimeEquals.");

    [Fact]
    public void ApiKeyHasher_HashIsTheUtf8EncodingOfTheArgon2String()
    {
        ApiKeyCredential credential = Hasher().Create();

        string encoded = Encoding.UTF8.GetString(credential.Hash);

        Assert.StartsWith("$argon2id$", encoded, StringComparison.Ordinal);
        Assert.True(Argon2PasswordHasher.Verify(credential.Token, encoded));
    }
}
