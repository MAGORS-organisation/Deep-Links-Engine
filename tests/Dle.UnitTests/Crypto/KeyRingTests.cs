using System.Text.Json;

using Dle.Crypto;
using Dle.Domain.Crypto;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Xunit;

namespace Dle.UnitTests.Crypto;

/// <summary>
/// The key ring and its rotation (§E.4.2, ADR-013, NFR-17).
/// </summary>
/// <remarks>
/// <para>
/// Two properties carry the weight here. The first is S-12: rotating a key must never invalidate a
/// signature made a moment earlier, so the outgoing key stays acceptable for the whole overlap
/// window rather than being closed at the instant it stops signing. The second is that the ring can
/// sign before any store has answered, because a control plane that cannot start until a database
/// replies is a control plane that cannot start during an incident.
/// </para>
/// <para>
/// Every test drives a fixed clock and a fake store, so a rotation window is asserted by arithmetic
/// rather than by waiting.
/// </para>
/// </remarks>
public sealed class KeyRingTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>An in-memory <see cref="ISigningKeyStore"/> that records what it was asked to do.</summary>
    private sealed class FakeKeyStore : ISigningKeyStore
    {
        internal List<SigningKeyMaterial> Keys { get; } = [];

        internal List<(string Kid, DateTimeOffset NotAfter)> Retirements { get; } = [];

        internal int LoadCount { get; private set; }

        public ValueTask<IReadOnlyList<SigningKeyMaterial>> LoadAsync(string purpose, CancellationToken ct)
        {
            LoadCount++;

            IReadOnlyList<SigningKeyMaterial> stored =
                [.. Keys.Where(k => string.Equals(k.Purpose, purpose, StringComparison.Ordinal))];

            return ValueTask.FromResult(stored);
        }

        public ValueTask SaveAsync(SigningKeyMaterial material, CancellationToken ct)
        {
            Keys.RemoveAll(k => string.Equals(k.Kid, material.Kid, StringComparison.Ordinal));
            Keys.Add(material);

            return ValueTask.CompletedTask;
        }

        public ValueTask RetireAsync(string kid, DateTimeOffset notAfter, CancellationToken ct)
        {
            Retirements.Add((kid, notAfter));

            int index = Keys.FindIndex(k => string.Equals(k.Kid, kid, StringComparison.Ordinal));

            if (index >= 0)
            {
                Keys[index] = Keys[index].Retire(notAfter);
            }

            return ValueTask.CompletedTask;
        }
    }

    private static CryptoOptions OptionsFor(string algorithm = SignatureAlgorithms.Hs256)
    {
        CryptoOptions options = new()
        {
            MasterSecret = CryptoTestKeys.MasterSecret,
            SigningAlgorithm = algorithm,
            KeyRotationDays = 90,
            KeyOverlapDays = 7,
        };

        return options;
    }

    private static KeyRing Create(
        CryptoOptions options,
        FakeKeyStore store,
        CryptoTestClock clock) =>
        new(Options.Create(options), store, clock, NullLogger<KeyRing>.Instance);

    // ------------------------------------------------------------------ starting without a store

    [Fact]
    public void Construction_WithOnlyAMasterSecret_ProducesASignerWithoutTouchingTheStore()
    {
        // A ring that needed the database to answer before it could sign would make the control
        // plane fail to start exactly when the database is under stress.
        FakeKeyStore store = new();
        using KeyRing ring = Create(OptionsFor(), store, new CryptoTestClock(Now));

        Assert.NotNull(ring.CurrentSigner);
        Assert.False(string.IsNullOrEmpty(ring.CurrentSigner.KeyId));
        Assert.Equal(0, store.LoadCount);
    }

    [Fact]
    public void Construction_DerivedBootstrapKey_IsIdenticalAcrossInstances()
    {
        // Two replicas share nothing but the master secret and must still verify each other's
        // signatures, so the derived key has to be a pure function of the configuration.
        using KeyRing first = Create(OptionsFor(), new FakeKeyStore(), new CryptoTestClock(Now));
        using KeyRing second = Create(OptionsFor(), new FakeKeyStore(), new CryptoTestClock(Now));

        Assert.Equal(first.CurrentSigner.KeyId, second.CurrentSigner.KeyId);

        byte[] payload = "replica-agreement"u8.ToArray();
        byte[] signature = first.CurrentSigner.Sign(payload);

        Assert.True(second.Verifier.Verify(
            second.CurrentSigner.AlgorithmId,
            first.CurrentSigner.KeyId,
            payload,
            signature));
    }

    [Fact]
    public void CurrentSigner_SignsSomethingItsOwnVerifierAccepts()
    {
        using KeyRing ring = Create(OptionsFor(), new FakeKeyStore(), new CryptoTestClock(Now));

        byte[] payload = "round-trip"u8.ToArray();
        byte[] signature = ring.CurrentSigner.Sign(payload);

        Assert.True(ring.Verifier.Verify(
            ring.CurrentSigner.AlgorithmId,
            ring.CurrentSigner.KeyId,
            payload,
            signature));
    }

    [Fact]
    public void Verifier_TamperedPayload_IsRefused()
    {
        using KeyRing ring = Create(OptionsFor(), new FakeKeyStore(), new CryptoTestClock(Now));

        byte[] signature = ring.CurrentSigner.Sign("original"u8.ToArray());

        Assert.False(ring.Verifier.Verify(
            ring.CurrentSigner.AlgorithmId,
            ring.CurrentSigner.KeyId,
            "tampered"u8.ToArray(),
            signature));
    }

    // ------------------------------------------------------------------ JWKS publication

    [Fact]
    public void GetJwks_SymmetricSigningKey_PublishesNothing()
    {
        // T-15: an HS256 key is the same secret on both sides. Publishing it would hand out the
        // signing key, so the ring must answer with an empty document rather than a key.
        using KeyRing ring = Create(OptionsFor(SignatureAlgorithms.Hs256), new FakeKeyStore(), new CryptoTestClock(Now));

        JwksDocument jwks = ring.GetJwks();

        Assert.NotNull(jwks);
        Assert.Empty(jwks.Keys);
    }

    [Fact]
    public async Task GetJwks_AsymmetricKey_PublishesThePublicHalfOnly()
    {
        FakeKeyStore store = new();
        CryptoTestClock clock = new(Now);

        SigningKeyMaterial ed25519 = SigningKeyFactory.Generate(
            SignatureAlgorithms.Ed25519,
            SigningKeyPurposes.Token,
            notBefore: Now.AddDays(-1),
            notAfter: Now.AddDays(30));

        store.Keys.Add(ed25519);

        CryptoOptions options = OptionsFor(SignatureAlgorithms.Ed25519);
        using KeyRing ring = Create(options, store, clock);
        await ring.RefreshAsync(CancellationToken.None);

        JwksDocument jwks = ring.GetJwks();

        JsonWebKey published = Assert.Single(jwks.Keys, key => string.Equals(key.Kid, ed25519.Kid, StringComparison.Ordinal));

        // Whatever the JWK carries, it must not be the private half.
        string encodedPrivate = Convert.ToBase64String(ed25519.PrivateKey);
        Assert.DoesNotContain(encodedPrivate, JsonSerializer.Serialize(published), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ refresh and merge

    [Fact]
    public async Task RefreshAsync_StoredKeys_JoinTheRing()
    {
        FakeKeyStore store = new();
        CryptoTestClock clock = new(Now);

        SigningKeyMaterial stored = SigningKeyFactory.Generate(
            SignatureAlgorithms.Hs256,
            SigningKeyPurposes.Token,
            notBefore: Now.AddDays(-1),
            notAfter: Now.AddDays(30));

        store.Keys.Add(stored);

        using KeyRing ring = Create(OptionsFor(), store, clock);

        Assert.DoesNotContain(stored.Kid, ring.KeyIds);

        await ring.RefreshAsync(CancellationToken.None);

        Assert.Contains(stored.Kid, ring.KeyIds);
    }

    [Fact]
    public async Task RefreshAsync_TheDerivedKey_SurvivesTheStoreTakingOver()
    {
        // Dropping it would invalidate every token signed while the store was still empty.
        FakeKeyStore store = new();
        CryptoTestClock clock = new(Now);

        using KeyRing ring = Create(OptionsFor(), store, clock);
        string derivedKid = ring.CurrentSigner.KeyId;

        store.Keys.Add(SigningKeyFactory.Generate(
            SignatureAlgorithms.Hs256,
            SigningKeyPurposes.Token,
            notBefore: Now,
            notAfter: Now.AddDays(30)));

        await ring.RefreshAsync(CancellationToken.None);

        Assert.Contains(derivedKid, ring.KeyIds);
    }

    [Fact]
    public async Task RefreshAsync_KeyOfAnotherPurpose_IsNotLoadedIntoTheTokenRing()
    {
        FakeKeyStore store = new();

        SigningKeyMaterial other = SigningKeyFactory.Generate(
            SignatureAlgorithms.Hs256,
            SigningKeyPurposes.Webhook,
            notBefore: Now,
            notAfter: Now.AddDays(30));

        store.Keys.Add(other);

        using KeyRing ring = Create(OptionsFor(), store, new CryptoTestClock(Now));
        await ring.RefreshAsync(CancellationToken.None);

        Assert.DoesNotContain(other.Kid, ring.KeyIds);
    }

    [Fact]
    public async Task RefreshAsync_ExpiredStoredKey_IsExcludedFromTheRing()
    {
        FakeKeyStore store = new();

        SigningKeyMaterial expired = SigningKeyFactory.Generate(
            SignatureAlgorithms.Hs256,
            SigningKeyPurposes.Token,
            notBefore: Now.AddDays(-40),
            notAfter: Now.AddDays(-1));

        store.Keys.Add(expired);

        using KeyRing ring = Create(OptionsFor(), store, new CryptoTestClock(Now));
        await ring.RefreshAsync(CancellationToken.None);

        Assert.DoesNotContain(expired.Kid, ring.KeyIds);
    }

    [Fact]
    public async Task RefreshAsync_UnreadableKey_IsSkippedWithoutTakingTheRingDown()
    {
        // One malformed row must cost that row, not the process. An HS256 key shorter than 32
        // bytes cannot build a verification key.
        FakeKeyStore store = new();

        store.Keys.Add(new SigningKeyMaterial
        {
            Kid = "malformed",
            AlgorithmId = SignatureAlgorithms.Hs256,
            PublicKey = [1, 2, 3],
            PrivateKey = [1, 2, 3],
            Purpose = SigningKeyPurposes.Token,
            NotBefore = Now.AddDays(-1),
            NotAfter = Now.AddDays(30),
        });

        SigningKeyMaterial healthy = SigningKeyFactory.Generate(
            SignatureAlgorithms.Hs256,
            SigningKeyPurposes.Token,
            notBefore: Now.AddDays(-1),
            notAfter: Now.AddDays(30));

        store.Keys.Add(healthy);

        using KeyRing ring = Create(OptionsFor(), store, new CryptoTestClock(Now));
        await ring.RefreshAsync(CancellationToken.None);

        Assert.DoesNotContain("malformed", ring.KeyIds);
        Assert.Contains(healthy.Kid, ring.KeyIds);
        Assert.NotNull(ring.CurrentSigner);
    }

    // ------------------------------------------------------------------ rotation, S-12

    [Fact]
    public async Task RotateAsync_MintsANewSigningKeyAndStoresIt()
    {
        FakeKeyStore store = new();
        CryptoTestClock clock = new(Now);

        using KeyRing ring = Create(OptionsFor(), store, clock);
        string before = ring.CurrentSigner.KeyId;

        await ring.RotateAsync(CancellationToken.None);

        Assert.NotEqual(before, ring.CurrentSigner.KeyId);
        Assert.Contains(store.Keys, k => string.Equals(k.Kid, ring.CurrentSigner.KeyId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RotateAsync_RetiresTheOutgoingKeyOneOverlapWindowIntoTheFuture()
    {
        // S-12 stated as arithmetic: the outgoing key stops being accepted at now + overlap,
        // never at now.
        FakeKeyStore store = new();
        CryptoTestClock clock = new(Now);

        CryptoOptions options = OptionsFor();
        options.KeyOverlapDays = 7;

        using KeyRing ring = Create(options, store, clock);
        string outgoing = ring.CurrentSigner.KeyId;

        await ring.RotateAsync(CancellationToken.None);

        (string kid, DateTimeOffset notAfter) = Assert.Single(store.Retirements);

        Assert.Equal(outgoing, kid);
        Assert.Equal(Now.AddDays(7), notAfter);
        Assert.True(notAfter > Now, "A retirement that closed the window immediately would break S-12.");
    }

    [Fact]
    public async Task RotateAsync_ASignatureMadeBeforeTheRotation_StillVerifiesAfterIt()
    {
        // The property S-12 actually protects, asserted end to end rather than through the store.
        FakeKeyStore store = new();
        CryptoTestClock clock = new(Now);

        using KeyRing ring = Create(OptionsFor(), store, clock);

        byte[] payload = "issued-before-rotation"u8.ToArray();
        string signingKid = ring.CurrentSigner.KeyId;
        string algorithm = ring.CurrentSigner.AlgorithmId;
        byte[] signature = ring.CurrentSigner.Sign(payload);

        await ring.RotateAsync(CancellationToken.None);

        Assert.NotEqual(signingKid, ring.CurrentSigner.KeyId);
        Assert.True(
            ring.Verifier.Verify(algorithm, signingKid, payload, signature),
            "A token signed a moment before the rotation must still verify (S-12).");
    }

    [Fact]
    public async Task RotateAsync_PastTheOverlapWindow_TheOldKeyStopsBeingAccepted()
    {
        FakeKeyStore store = new();
        CryptoTestClock clock = new(Now);

        CryptoOptions options = OptionsFor();
        options.KeyOverlapDays = 7;

        using KeyRing ring = Create(options, store, clock);

        byte[] payload = "issued-before-rotation"u8.ToArray();
        string signingKid = ring.CurrentSigner.KeyId;
        string algorithm = ring.CurrentSigner.AlgorithmId;
        byte[] signature = ring.CurrentSigner.Sign(payload);

        await ring.RotateAsync(CancellationToken.None);

        clock.Now = Now.AddDays(8);
        await ring.RefreshAsync(CancellationToken.None);

        Assert.False(
            ring.Verifier.Verify(algorithm, signingKid, payload, signature),
            "Once the overlap has elapsed the retired key must stop being accepted.");
    }

    [Fact]
    public async Task RotateAsync_Twice_RetiresEachOutgoingKeyInTurn()
    {
        FakeKeyStore store = new();
        CryptoTestClock clock = new(Now);

        using KeyRing ring = Create(OptionsFor(), store, clock);
        string first = ring.CurrentSigner.KeyId;

        await ring.RotateAsync(CancellationToken.None);
        string second = ring.CurrentSigner.KeyId;

        clock.Now = Now.AddDays(1);
        await ring.RotateAsync(CancellationToken.None);
        string third = ring.CurrentSigner.KeyId;

        Assert.Equal(2, store.Retirements.Count);
        Assert.Equal(first, store.Retirements[0].Kid);
        Assert.Equal(second, store.Retirements[1].Kid);
        Assert.NotEqual(second, third);
    }

    [Fact]
    public async Task RotateAsync_NewKeyWindow_CoversRotationPlusOverlap()
    {
        FakeKeyStore store = new();
        CryptoTestClock clock = new(Now);

        CryptoOptions options = OptionsFor();
        options.KeyRotationDays = 90;
        options.KeyOverlapDays = 7;

        using KeyRing ring = Create(options, store, clock);
        await ring.RotateAsync(CancellationToken.None);

        SigningKeyMaterial minted = Assert.Single(
            store.Keys,
            k => string.Equals(k.Kid, ring.CurrentSigner.KeyId, StringComparison.Ordinal));

        Assert.Equal(Now, minted.NotBefore);
        Assert.Equal(Now.AddDays(97), minted.NotAfter);
    }

    [Fact]
    public async Task RotateAsync_IsSafeToCallConcurrently()
    {
        FakeKeyStore store = new();
        CryptoTestClock clock = new(Now);

        using KeyRing ring = Create(OptionsFor(), store, clock);

        await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => ring.RotateAsync(CancellationToken.None).AsTask()));

        Assert.NotNull(ring.CurrentSigner);
        Assert.Contains(store.Keys, k => string.Equals(k.Kid, ring.CurrentSigner.KeyId, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ guards

    [Fact]
    public void Construction_NullArgument_Throws()
    {
        CryptoTestClock clock = new(Now);
        FakeKeyStore store = new();
        IOptions<CryptoOptions> options = Options.Create(OptionsFor());

        Assert.Throws<ArgumentNullException>(() => new KeyRing(null!, store, clock, NullLogger<KeyRing>.Instance));
        Assert.Throws<ArgumentNullException>(() => new KeyRing(options, null!, clock, NullLogger<KeyRing>.Instance));
        Assert.Throws<ArgumentNullException>(() => new KeyRing(options, store, null!, NullLogger<KeyRing>.Instance));
        Assert.Throws<ArgumentNullException>(() => new KeyRing(options, store, clock, null!));
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        KeyRing ring = Create(OptionsFor(), new FakeKeyStore(), new CryptoTestClock(Now));

        ring.Dispose();
        ring.Dispose();
    }
}
