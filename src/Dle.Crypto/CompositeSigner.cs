namespace Dle.Crypto;

/// <summary>
/// Produces the <see cref="SignatureAlgorithms.Ed25519MlDsa65"/> composite signature: an Ed25519
/// signature and an ML-DSA-65 signature over the same payload, both of which must verify.
/// </summary>
/// <remarks>
/// This is the shape §E.5.3 names as the phase 2 default, and the reason it is hybrid rather than
/// pure post-quantum is in §E.5.4: ANSSI and the EU roadmap both recommend hybrids precisely
/// because the lattice schemes are the less scrutinised half. A verifier that requires both
/// signatures is no weaker than the stronger of the two, so adopting ML-DSA cannot regress
/// security if the lattice assumption turns out to be wrong.
/// </remarks>
public sealed class CompositeSigner : ISigner
{
    private readonly ISigner _classical;
    private readonly ISigner _postQuantum;

    /// <summary>
    /// Creates a composite signer.
    /// </summary>
    /// <param name="keyId">Identifier written into the token. Both components share it, because
    /// they are one key from the ring's point of view.</param>
    /// <param name="classical">The Ed25519 component.</param>
    /// <param name="postQuantum">The ML-DSA-65 component.</param>
    /// <exception cref="ArgumentNullException">A component is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="keyId"/> is empty, or a component does
    /// not implement the algorithm the composite is defined over.</exception>
    public CompositeSigner(string keyId, ISigner classical, ISigner postQuantum)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyId);
        ArgumentNullException.ThrowIfNull(classical);
        ArgumentNullException.ThrowIfNull(postQuantum);

        if (!string.Equals(classical.AlgorithmId, SignatureAlgorithms.Ed25519, StringComparison.Ordinal))
        {
            throw new ArgumentException("The classical component of the composite must be Ed25519.", nameof(classical));
        }

        if (!string.Equals(postQuantum.AlgorithmId, SignatureAlgorithms.MlDsa65, StringComparison.Ordinal))
        {
            throw new ArgumentException("The post-quantum component of the composite must be ML-DSA-65.", nameof(postQuantum));
        }

        KeyId = keyId;
        _classical = classical;
        _postQuantum = postQuantum;
    }

    /// <inheritdoc />
    public string AlgorithmId => SignatureAlgorithms.Ed25519MlDsa65;

    /// <inheritdoc />
    public string KeyId { get; }

    /// <inheritdoc />
    public int MaxSignatureSize =>
        CompositeEncoding.PrefixLength + _classical.MaxSignatureSize + _postQuantum.MaxSignatureSize;

    /// <inheritdoc />
    public byte[] Sign(ReadOnlySpan<byte> payload) =>
        CompositeEncoding.Join(_classical.Sign(payload), _postQuantum.Sign(payload));
}
