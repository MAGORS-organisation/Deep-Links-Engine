namespace Dle.Crypto;

/// <summary>
/// Verifies signatures made with any algorithm and key the deployment currently accepts (§E.4.2).
/// </summary>
/// <remarks>
/// <para>
/// The signer uses one algorithm; the verifier accepts a set. That asymmetry is what makes an
/// algorithm migration possible without an outage: for the length of the migration the set holds
/// both the outgoing and the incoming algorithm, and only then does the signer switch.
/// </para>
/// <para>
/// The set is closed. An algorithm outside it is refused before any signature work happens, which
/// is what stops the classic downgrade: rewrite the <c>alg</c> segment of a token to something
/// weak, or to <c>none</c>, and hope the verifier follows the token instead of its own policy.
/// </para>
/// <para>
/// Instances are immutable. <see cref="KeyRing"/> builds a new one whenever the set of keys
/// changes, rather than mutating a shared one, so a verification in flight never sees half a
/// rotation.
/// </para>
/// </remarks>
public sealed class MultiAlgorithmVerifier : IVerifier
{
    private readonly HashSet<string> _accepted;
    private readonly Dictionary<string, VerificationKey> _keys;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates a verifier.
    /// </summary>
    /// <param name="acceptedAlgorithms">Algorithms to accept. Anything else is refused.</param>
    /// <param name="keys">Keys the verifier may use. A duplicate identifier keeps the first key,
    /// so a stale row cannot displace the one already in use.</param>
    /// <param name="timeProvider">Clock used to test validity windows. SHARED-KERNEL §17.2 rules
    /// out reading the machine clock directly.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public MultiAlgorithmVerifier(
        IEnumerable<string> acceptedAlgorithms,
        IEnumerable<VerificationKey> keys,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(acceptedAlgorithms);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _accepted = new HashSet<string>(acceptedAlgorithms, StringComparer.Ordinal);
        _keys = new Dictionary<string, VerificationKey>(StringComparer.Ordinal);
        _timeProvider = timeProvider;

        foreach (VerificationKey key in keys)
        {
            _keys.TryAdd(key.Kid, key);
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> AcceptedAlgorithms => _accepted;

    /// <summary>Key identifiers this verifier knows about.</summary>
    public IReadOnlyCollection<string> KeyIds => _keys.Keys;

    /// <inheritdoc />
    public bool Verify(string algorithmId, string keyId, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature)
    {
        if (string.IsNullOrEmpty(algorithmId) || string.IsNullOrEmpty(keyId))
        {
            return false;
        }

        if (!_accepted.Contains(algorithmId))
        {
            return false;
        }

        if (!_keys.TryGetValue(keyId, out VerificationKey? key))
        {
            return false;
        }

        // A key is bound to one algorithm. Without this check a token could name a key that
        // exists and an algorithm that is accepted but is not the one the key was issued for,
        // which is a confusion attack rather than a mismatch.
        if (!string.Equals(key.AlgorithmId, algorithmId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!key.IsValidAt(_timeProvider.GetUtcNow()))
        {
            return false;
        }

        return key.Verify(payload, signature);
    }
}
