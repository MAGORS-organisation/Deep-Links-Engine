using Dle.Crypto;
using Dle.Domain.Crypto;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Xunit;

namespace Dle.UnitTests.Crypto;

/// <summary>
/// Start-up validation of the crypto options, the configuration backed key store and the
/// process-local replay guard (§E.4.2, §E.5.3, SHARED-KERNEL §16).
/// </summary>
/// <remarks>
/// The theme is failing at start rather than under load. A mistyped key, an algorithm with no
/// implementation, or a post-quantum algorithm switched on by accident are all mistakes that would
/// otherwise surface as an exception on the first request that needed to sign — long after the
/// deployment looked healthy.
/// </remarks>
public sealed class CryptoConfigurationTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static CryptoOptions Valid() => new()
    {
        MasterSecret = CryptoTestKeys.MasterSecret,
        SigningAlgorithm = SignatureAlgorithms.Hs256,
    };

    private static CryptoOptionsValidator Validator => new();

    private static SigningKeyOptions KeyOptions(
        string kid,
        string algorithm = SignatureAlgorithms.Hs256,
        byte[]? privateKey = null) => new()
        {
            Kid = kid,
            Algorithm = algorithm,
            PrivateKey = Convert.ToBase64String(privateKey ?? CryptoTestKeys.Primary),
            Purpose = SigningKeyPurposes.Token,
        };

    // ------------------------------------------------------------------ options validation

    [Fact]
    public void Validate_DefaultOptions_Succeed()
    {
        Assert.True(Validator.Validate(null, Valid()).Succeeded);
    }

    [Fact]
    public void Validate_UnimplementedSigningAlgorithm_IsRefusedAndNamesTheSupportedSet()
    {
        CryptoOptions options = Valid();
        options.SigningAlgorithm = "RS256";

        ValidateOptionsResult result = Validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("RS256", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, f => f.Contains(SignatureAlgorithms.Hs256, StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_PostQuantumSigningAlgorithmWithoutTheFlag_IsRefused()
    {
        // §E.5.3 keeps the post-quantum algorithms off in phase 0; turning one on by editing the
        // algorithm alone must not quietly work.
        CryptoOptions options = Valid();
        options.SigningAlgorithm = SignatureAlgorithms.MlDsa65;
        options.HybridPqEnabled = false;

        ValidateOptionsResult result = Validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("HybridPqEnabled", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_PostQuantumSigningAlgorithmWithTheFlag_IsAccepted()
    {
        CryptoOptions options = Valid();
        options.SigningAlgorithm = SignatureAlgorithms.MlDsa65;
        options.HybridPqEnabled = true;

        Assert.True(Validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void Validate_SlhDsaWithoutAConfiguredKey_IsRefusedBecauseItCannotBeDerived()
    {
        CryptoOptions options = Valid();
        options.SigningAlgorithm = SignatureAlgorithms.SlhDsa128s;
        options.HybridPqEnabled = true;

        ValidateOptionsResult result = Validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Dle:Crypto:Keys", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_UnimplementedAcceptedAlgorithm_IsRefused()
    {
        CryptoOptions options = Valid();
        options.AcceptedAlgorithms.Add("RS256");

        ValidateOptionsResult result = Validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("AcceptedAlgorithms", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_PostQuantumAcceptedAlgorithmWithoutTheFlag_IsRefused()
    {
        CryptoOptions options = Valid();
        options.AcceptedAlgorithms.Add(SignatureAlgorithms.MlDsa65);

        ValidateOptionsResult result = Validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("HybridPqEnabled", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_Argon2BelowTheOwaspFloor_IsRefused()
    {
        CryptoOptions options = Valid();
        options.Argon2.MemoryKib = 16;

        ValidateOptionsResult result = Validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Argon2", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_DuplicateKid_IsRefused()
    {
        CryptoOptions options = Valid();
        options.Keys.Add(KeyOptions("shared"));
        options.Keys.Add(KeyOptions("shared", privateKey: CryptoTestKeys.Secondary));

        ValidateOptionsResult result = Validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("more than one key", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_KidContainingADot_IsRefusedBecauseItWouldSplitTheToken()
    {
        // The token format is dlt1.<alg>.<kid>.<payload>.<signature>; a dot inside the kid would
        // make the segments ambiguous.
        CryptoOptions options = Valid();
        options.Keys.Add(KeyOptions("has.dot"));

        Assert.True(Validator.Validate(null, options).Failed);
    }

    [Fact]
    public void Validate_KeyWithAnUnimplementedAlgorithm_IsRefused()
    {
        CryptoOptions options = Valid();
        options.Keys.Add(KeyOptions("k1", algorithm: "RS256"));

        ValidateOptionsResult result = Validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("RS256", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_KeyWindowEndingBeforeItStarts_IsRefused()
    {
        CryptoOptions options = Valid();
        SigningKeyOptions key = KeyOptions("k1");
        key.NotBefore = Now;
        key.NotAfter = Now.AddDays(-1);
        options.Keys.Add(key);

        ValidateOptionsResult result = Validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("NotAfter", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_KeyThatIsNotBase64_IsRefusedAtStartRatherThanAtFirstUse()
    {
        CryptoOptions options = Valid();
        SigningKeyOptions key = KeyOptions("k1");
        key.PrivateKey = "not base64 at all!!";
        options.Keys.Add(key);

        Assert.True(Validator.Validate(null, options).Failed);
    }

    [Fact]
    public void Validate_ValidConfiguredKey_IsAccepted()
    {
        CryptoOptions options = Valid();
        options.Keys.Add(KeyOptions("k1"));

        Assert.True(Validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void Validate_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Validator.Validate(null, null!));
    }

    // ------------------------------------------------------------------ configuration key store

    [Fact]
    public async Task ConfigurationStore_LoadsOnlyTheRequestedPurpose()
    {
        CryptoOptions options = Valid();
        options.Keys.Add(KeyOptions("token-key"));

        SigningKeyOptions webhook = KeyOptions("webhook-key", privateKey: CryptoTestKeys.Secondary);
        webhook.Purpose = SigningKeyPurposes.Webhook;
        options.Keys.Add(webhook);

        ConfigurationSigningKeyStore store = new(
            Options.Create(options),
            NullLogger<ConfigurationSigningKeyStore>.Instance);

        IReadOnlyList<SigningKeyMaterial> tokens = await store.LoadAsync(SigningKeyPurposes.Token, CancellationToken.None);

        Assert.Equal("token-key", Assert.Single(tokens).Kid);
    }

    [Fact]
    public async Task ConfigurationStore_SaveThenLoad_ReturnsTheSavedKey()
    {
        ConfigurationSigningKeyStore store = new(
            Options.Create(Valid()),
            NullLogger<ConfigurationSigningKeyStore>.Instance);

        SigningKeyMaterial minted = SigningKeyFactory.Generate(
            SignatureAlgorithms.Hs256,
            SigningKeyPurposes.Token,
            notBefore: Now,
            notAfter: Now.AddDays(30));

        await store.SaveAsync(minted, CancellationToken.None);

        IReadOnlyList<SigningKeyMaterial> loaded = await store.LoadAsync(SigningKeyPurposes.Token, CancellationToken.None);

        Assert.Equal(minted.Kid, Assert.Single(loaded).Kid);
    }

    [Fact]
    public async Task ConfigurationStore_Retire_ClosesTheWindowWithoutRemovingTheKey()
    {
        // Retiring is not deleting: the key has to keep verifying until its window closes (S-12).
        CryptoOptions options = Valid();
        options.Keys.Add(KeyOptions("k1"));

        ConfigurationSigningKeyStore store = new(
            Options.Create(options),
            NullLogger<ConfigurationSigningKeyStore>.Instance);

        await store.RetireAsync("k1", Now.AddDays(7), CancellationToken.None);

        SigningKeyMaterial retired = Assert.Single(
            await store.LoadAsync(SigningKeyPurposes.Token, CancellationToken.None));

        Assert.Equal(Now.AddDays(7), retired.NotAfter);
        Assert.False(retired.IsCurrent);
    }

    [Fact]
    public async Task ConfigurationStore_RetireAnUnknownKid_IsANoOp()
    {
        ConfigurationSigningKeyStore store = new(
            Options.Create(Valid()),
            NullLogger<ConfigurationSigningKeyStore>.Instance);

        await store.RetireAsync("never-seen", Now.AddDays(7), CancellationToken.None);

        Assert.Empty(await store.LoadAsync(SigningKeyPurposes.Token, CancellationToken.None));
    }

    [Fact]
    public async Task ConfigurationStore_CancelledToken_Throws()
    {
        ConfigurationSigningKeyStore store = new(
            Options.Create(Valid()),
            NullLogger<ConfigurationSigningKeyStore>.Instance);

        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await store.LoadAsync(SigningKeyPurposes.Token, cts.Token));
    }

    // ------------------------------------------------------------------ replay guard

    [Fact]
    public async Task ReplayGuard_FirstUseOfAnIdentifier_IsAccepted()
    {
        CryptoTestClock clock = new(Now);
        InMemoryTokenReplayGuard guard = new(1024, clock, NullLogger<InMemoryTokenReplayGuard>.Instance);

        Assert.True(await guard.TryConsumeAsync("jti-1", Now.AddMinutes(5), CancellationToken.None));
    }

    [Fact]
    public async Task ReplayGuard_SecondUseOfTheSameIdentifier_IsRefused()
    {
        CryptoTestClock clock = new(Now);
        InMemoryTokenReplayGuard guard = new(1024, clock, NullLogger<InMemoryTokenReplayGuard>.Instance);

        Assert.True(await guard.TryConsumeAsync("jti-1", Now.AddMinutes(5), CancellationToken.None));
        Assert.False(await guard.TryConsumeAsync("jti-1", Now.AddMinutes(5), CancellationToken.None));
    }

    [Fact]
    public async Task ReplayGuard_AlreadyExpiredToken_IsRefusedAndNotRemembered()
    {
        CryptoTestClock clock = new(Now);
        InMemoryTokenReplayGuard guard = new(1024, clock, NullLogger<InMemoryTokenReplayGuard>.Instance);

        Assert.False(await guard.TryConsumeAsync("stale", Now.AddSeconds(-1), CancellationToken.None));
        Assert.Equal(0, guard.Count);
    }

    [Fact]
    public async Task ReplayGuard_AtCapacity_PrunesExpiredEntriesRatherThanRefusing()
    {
        CryptoTestClock clock = new(Now);
        InMemoryTokenReplayGuard guard = new(2, clock, NullLogger<InMemoryTokenReplayGuard>.Instance);

        Assert.True(await guard.TryConsumeAsync("a", Now.AddMinutes(1), CancellationToken.None));
        Assert.True(await guard.TryConsumeAsync("b", Now.AddMinutes(1), CancellationToken.None));

        clock.Now = Now.AddMinutes(2);

        Assert.True(await guard.TryConsumeAsync("c", clock.Now.AddMinutes(1), CancellationToken.None));
        Assert.Equal(1, guard.Count);
    }

    [Fact]
    public async Task ReplayGuard_FullOfLiveEntries_RefusesRatherThanForgetting()
    {
        // SHARED-KERNEL §17.9: the default is deny. Forgetting an identifier it promised to
        // remember would turn a full guard into a replay window.
        CryptoTestClock clock = new(Now);
        InMemoryTokenReplayGuard guard = new(2, clock, NullLogger<InMemoryTokenReplayGuard>.Instance);

        Assert.True(await guard.TryConsumeAsync("a", Now.AddMinutes(10), CancellationToken.None));
        Assert.True(await guard.TryConsumeAsync("b", Now.AddMinutes(10), CancellationToken.None));

        Assert.False(await guard.TryConsumeAsync("c", Now.AddMinutes(10), CancellationToken.None));
        Assert.Equal(2, guard.Count);
    }

    [Fact]
    public void ReplayGuard_NonPositiveCapacity_Throws()
    {
        CryptoTestClock clock = new(Now);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new InMemoryTokenReplayGuard(0, clock, NullLogger<InMemoryTokenReplayGuard>.Instance));
    }

    [Fact]
    public async Task ReplayGuard_EmptyIdentifier_Throws()
    {
        CryptoTestClock clock = new(Now);
        InMemoryTokenReplayGuard guard = new(16, clock, NullLogger<InMemoryTokenReplayGuard>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await guard.TryConsumeAsync("", Now.AddMinutes(1), CancellationToken.None));
    }

    // ------------------------------------------------------------------ key material

    [Fact]
    public void IsValidAt_UnboundedWindow_AcceptsEveryInstant()
    {
        SigningKeyMaterial material = new()
        {
            Kid = "k",
            AlgorithmId = SignatureAlgorithms.Hs256,
            PublicKey = CryptoTestKeys.Primary,
        };

        Assert.True(material.IsValidAt(DateTimeOffset.MinValue));
        Assert.True(material.IsValidAt(Now));
        Assert.True(material.IsValidAt(DateTimeOffset.MaxValue));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(5, true)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    public void IsValidAt_BoundedWindow_IsInclusiveAtBothEnds(int offsetDays, bool expected)
    {
        SigningKeyMaterial material = new()
        {
            Kid = "k",
            AlgorithmId = SignatureAlgorithms.Hs256,
            PublicKey = CryptoTestKeys.Primary,
            NotBefore = Now,
            NotAfter = Now.AddDays(10),
        };

        Assert.Equal(expected, material.IsValidAt(Now.AddDays(offsetDays)));
    }

    [Fact]
    public void Retire_UnboundedKey_GainsTheGivenEndAndStopsBeingCurrent()
    {
        SigningKeyMaterial material = new()
        {
            Kid = "k",
            AlgorithmId = SignatureAlgorithms.Hs256,
            PublicKey = CryptoTestKeys.Primary,
            IsCurrent = true,
        };

        SigningKeyMaterial retired = material.Retire(Now.AddDays(7));

        Assert.Equal(Now.AddDays(7), retired.NotAfter);
        Assert.False(retired.IsCurrent);
        Assert.Equal(material.Kid, retired.Kid);
    }

    [Fact]
    public void Retire_KeyThatAlreadyEndsEarlier_KeepsTheEarlierEnd()
    {
        // Retiring must never extend a window that was already closing.
        SigningKeyMaterial material = new()
        {
            Kid = "k",
            AlgorithmId = SignatureAlgorithms.Hs256,
            PublicKey = CryptoTestKeys.Primary,
            NotAfter = Now.AddDays(2),
        };

        Assert.Equal(Now.AddDays(2), material.Retire(Now.AddDays(7)).NotAfter);
    }
}
