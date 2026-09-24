namespace Dle.Domain.Crypto;

/// <summary>
/// Verifies signatures produced by any algorithm and key the deployment currently accepts.
/// </summary>
/// <remarks>
/// The accepted set is configuration, not code. During a rotation it holds both the outgoing and
/// the incoming algorithm; an algorithm that is not in the set is refused without being tried,
/// which is what stops an attacker from downgrading a token by rewriting its <c>alg</c> segment.
/// </remarks>
public interface IVerifier
{
    /// <summary>Verifies a signature.</summary>
    /// <param name="algorithmId">Algorithm named by the token. Refused unless it is in
    /// <see cref="AcceptedAlgorithms"/>.</param>
    /// <param name="keyId">Key named by the token.</param>
    /// <param name="payload">The exact bytes that were signed.</param>
    /// <param name="signature">The signature to check.</param>
    /// <returns><see langword="true"/> only when the algorithm is accepted, the key is known and
    /// current, and the signature verifies. Every other outcome, including an unknown key or a
    /// malformed signature, returns <see langword="false"/> rather than throwing.</returns>
    bool Verify(string algorithmId, string keyId, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature);

    /// <summary>Algorithm identifiers this verifier will consider.</summary>
    IReadOnlyCollection<string> AcceptedAlgorithms { get; }
}
