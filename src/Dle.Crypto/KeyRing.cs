using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dle.Crypto;

/// <summary>
/// Holds the signing material of a deployment and owns its rotation (§E.4.2, ADR-013).
/// </summary>
/// <remarks>
/// <para>
/// The ring keeps an immutable snapshot and swaps it wholesale. A verification in flight therefore
/// never observes half a rotation, and no lock is taken on the request path.
/// </para>
/// <para>
/// The first snapshot is built in the constructor, from configuration only: the derived bootstrap
/// key plus anything under <c>Dle:Crypto:Keys</c>. No I/O happens, so the ring can sign before the
/// database has answered anything, and a slow database cannot stop the process from starting.
/// <see cref="RefreshAsync"/> then folds in whatever the <see cref="ISigningKeyStore"/> holds.
/// </para>
/// <para>
/// Rotation never shortens a window that is still open (S-12). The outgoing key is retired with
/// <c>NotAfter = now + KeyOverlapDays</c>, which has to exceed the lifetime of the longest lived
/// artefact it signed; until then it stays in the verifier and in the published JWKS, because a
/// webhook receiver that fetched the key set an hour ago must still be able to check what it
/// received a minute ago.
/// </para>
/// </remarks>
public sealed class KeyRing : IKeyRing, IDisposable
{
    private readonly ISigningKeyStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<KeyRing> _logger;
    private readonly CryptoOptions _options;
    private readonly SigningKeyMaterial _fallbackSigningKey;
    private readonly SemaphoreSlim _rotationLock = new(1, 1);

    /// <summary>
    /// The keys that came from configuration, including the derived bootstrap key. Swapped as a
    /// whole rather than mutated, because <see cref="RefreshAsync"/> enumerates it without a lock.
    /// </summary>
    private SigningKeyMaterial[] _configuredKeys;

    private KeyRingSnapshot _snapshot;

    /// <summary>
    /// Creates the ring and builds its first snapshot from configuration.
    /// </summary>
    /// <param name="options">The crypto options.</param>
    /// <param name="store">Durable key store, consulted by <see cref="RefreshAsync"/>.</param>
    /// <param name="timeProvider">Clock. SHARED-KERNEL §17.2 rules out reading the machine clock.</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">No key is available for the configured
    /// algorithm and none can be derived from the master secret.</exception>
    public KeyRing(
        IOptions<CryptoOptions> options,
        ISigningKeyStore store,
        TimeProvider timeProvider,
        ILogger<KeyRing> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _store = store;
        _timeProvider = timeProvider;
        _logger = logger;

        DateTimeOffset now = timeProvider.GetUtcNow();
        List<SigningKeyMaterial> configured = [];

        foreach (SigningKeyOptions key in _options.Keys)
        {
            if (string.Equals(key.Purpose, SigningKeyPurposes.Token, StringComparison.Ordinal))
            {
                configured.Add(SigningKeyFactory.FromOptions(key));
            }
        }

        SigningKeyMaterial? configuredSigner = configured.Find(k =>
            string.Equals(k.AlgorithmId, _options.SigningAlgorithm, StringComparison.Ordinal) &&
            k.PrivateKey.Length > 0 &&
            k.IsValidAt(now));

        // The key is only derived when nothing configured can sign. Deriving it unconditionally
        // would put an unused secret in memory for no reason.
        _fallbackSigningKey = configuredSigner ?? SigningKeyFactory.DeriveBootstrapKey(_options, now);

        if (configuredSigner is null)
        {
            configured.Add(_fallbackSigningKey);
        }

        _configuredKeys = [.. configured];
        _snapshot = Build(_configuredKeys, now);
    }

    /// <summary>Releases the rotation lock.</summary>
    public void Dispose() => _rotationLock.Dispose();

    /// <inheritdoc />
    public ISigner CurrentSigner => Volatile.Read(ref _snapshot).Signer;

    /// <inheritdoc />
    public IVerifier Verifier => Volatile.Read(ref _snapshot).Verifier;

    /// <summary>Identifiers of every key the ring currently accepts.</summary>
    public IReadOnlyCollection<string> KeyIds => Volatile.Read(ref _snapshot).Verifier.KeyIds;

    /// <inheritdoc />
    public JwksDocument GetJwks() => Volatile.Read(ref _snapshot).Jwks;

    /// <summary>
    /// Reloads the ring from the key store, merging in the configured keys.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes once the new snapshot is visible.</returns>
    /// <remarks>
    /// Call this after another instance rotated, or on a schedule. It is safe to call
    /// concurrently; the snapshot is replaced atomically.
    /// </remarks>
    public async ValueTask RefreshAsync(CancellationToken ct)
    {
        IReadOnlyList<SigningKeyMaterial> stored = await _store.LoadAsync(SigningKeyPurposes.Token, ct);
        DateTimeOffset now = _timeProvider.GetUtcNow();

        List<SigningKeyMaterial> merged = [.. stored];

        // Configured keys, the derived one included, stay in the ring even once the store has
        // taken over. Dropping them would invalidate every token they signed while the store was
        // still empty, and would silently disable a key an operator put in configuration on
        // purpose. The store wins on a shared identifier, because it is the durable source.
        foreach (SigningKeyMaterial configured in Volatile.Read(ref _configuredKeys))
        {
            if (!merged.Exists(k => string.Equals(k.Kid, configured.Kid, StringComparison.Ordinal)))
            {
                merged.Add(configured);
            }
        }

        Volatile.Write(ref _snapshot, Build(merged, now));
    }

    /// <inheritdoc />
    public async ValueTask RotateAsync(CancellationToken ct)
    {
        await _rotationLock.WaitAsync(ct);

        try
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            TimeSpan overlap = TimeSpan.FromDays(_options.KeyOverlapDays);

            SigningKeyMaterial replacement = SigningKeyFactory.Generate(
                _options.SigningAlgorithm,
                SigningKeyPurposes.Token,
                notBefore: now,
                notAfter: now + TimeSpan.FromDays(_options.KeyRotationDays) + overlap);

            await _store.SaveAsync(replacement, ct);

            string outgoingKid = CurrentSigner.KeyId;
            DateTimeOffset outgoingNotAfter = now + overlap;

            if (!string.Equals(outgoingKid, replacement.Kid, StringComparison.Ordinal))
            {
                // The outgoing key keeps verifying for the whole overlap. Closing its window now
                // would invalidate signatures made seconds ago, which is exactly what S-12 forbids.
                await _store.RetireAsync(outgoingKid, outgoingNotAfter, ct);

                // The outgoing key may never have reached the store: the derived bootstrap key and
                // anything under Dle:Crypto:Keys live in configuration only, so the call above
                // matched no row. Those copies also have to be retired here, otherwise RefreshAsync
                // merges the original — unbounded — window straight back in and the key stays
                // acceptable for ever, which would make rotating away from a leaked bootstrap key
                // impossible (S-12).
                RetireConfiguredKey(outgoingKid, outgoingNotAfter);
            }

            await RefreshAsync(ct);

            if (_logger.IsEnabled(LogLevel.Information))
            {
                // Guarded because the instant has to be boxed into the argument array, which
                // CA1873 asks callers not to pay for when the level is switched off.
                _logger.LogInformation(
                    "Rotated the {Purpose} signing key to {Kid} ({Algorithm}). The previous key {OutgoingKid} stays acceptable until {NotAfter:O}.",
                    SigningKeyPurposes.Token,
                    replacement.Kid,
                    replacement.AlgorithmId,
                    outgoingKid,
                    outgoingNotAfter);
            }
        }
        finally
        {
            _rotationLock.Release();
        }
    }

    /// <summary>
    /// Closes the acceptance window of a configuration supplied key, if the rotated key was one.
    /// </summary>
    /// <param name="kid">Identifier of the outgoing key.</param>
    /// <param name="notAfter">Instant the key stops being accepted, always in the future.</param>
    /// <remarks>
    /// Called under the rotation lock, and it replaces the array rather than editing it in place so
    /// that a <see cref="RefreshAsync"/> running on another thread keeps enumerating a stable
    /// snapshot.
    /// </remarks>
    private void RetireConfiguredKey(string kid, DateTimeOffset notAfter)
    {
        SigningKeyMaterial[] current = Volatile.Read(ref _configuredKeys);
        int index = Array.FindIndex(current, k => string.Equals(k.Kid, kid, StringComparison.Ordinal));

        if (index < 0)
        {
            return;
        }

        SigningKeyMaterial[] replacement = [.. current];
        replacement[index] = current[index].Retire(notAfter);

        Volatile.Write(ref _configuredKeys, replacement);
    }

    /// <summary>
    /// Assembles a snapshot: one signer, a verifier over every key still inside its window, and
    /// the JWKS built from the same set, so what is published is exactly what will be accepted.
    /// </summary>
    private KeyRingSnapshot Build(IReadOnlyList<SigningKeyMaterial> materials, DateTimeOffset now)
    {
        List<VerificationKey> verificationKeys = [];
        List<JsonWebKey> jwks = [];
        SigningKeyMaterial? signingKey = null;

        foreach (SigningKeyMaterial material in materials)
        {
            if (material.NotAfter is not null && material.NotAfter.Value < now)
            {
                continue;
            }

            VerificationKey verificationKey;

            try
            {
                verificationKey = SigningKeyFactory.CreateVerificationKey(material);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException)
            {
                // One unreadable row must not take the whole ring down: the other keys still
                // verify, and the operator gets a named diagnostic instead of a failed start.
                _logger.LogError(
                    e,
                    "Signing key {Kid} ({Algorithm}) could not be loaded and is excluded from the key ring.",
                    material.Kid,
                    material.AlgorithmId);
                continue;
            }

            verificationKeys.Add(verificationKey);

            JsonWebKey? jwk = verificationKey.ToJsonWebKey();

            if (jwk is not null)
            {
                jwks.Add(jwk);
            }

            bool canSign =
                material.PrivateKey.Length > 0 &&
                material.IsValidAt(now) &&
                string.Equals(material.AlgorithmId, _options.SigningAlgorithm, StringComparison.Ordinal);

            if (canSign && (signingKey is null || (material.IsCurrent && !signingKey.IsCurrent)))
            {
                signingKey = material;
            }
        }

        signingKey ??= _fallbackSigningKey;

        HashSet<string> accepted = new(_options.AcceptedAlgorithms, StringComparer.Ordinal)
        {
            signingKey.AlgorithmId,
        };

        return new KeyRingSnapshot(
            SigningKeyFactory.CreateSigner(signingKey),
            new MultiAlgorithmVerifier(accepted, verificationKeys, _timeProvider),
            new JwksDocument(jwks));
    }

    /// <summary>An immutable view of the ring, replaced as a whole on every change.</summary>
    private sealed record KeyRingSnapshot(ISigner Signer, MultiAlgorithmVerifier Verifier, JwksDocument Jwks);
}
