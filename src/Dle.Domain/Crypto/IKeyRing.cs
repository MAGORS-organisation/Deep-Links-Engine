namespace Dle.Domain.Crypto;

/// <summary>
/// Holds the signing material of a deployment and owns its rotation (§E.4.2, K4 and K7 of the
/// cryptographic inventory).
/// </summary>
public interface IKeyRing
{
    /// <summary>The signer to use right now. Exactly one algorithm and one key at a time.</summary>
    ISigner CurrentSigner { get; }

    /// <summary>The verifier, which accepts every algorithm and key still in the acceptance
    /// window, not only the current one.</summary>
    IVerifier Verifier { get; }

    /// <summary>Builds the public key set for publication.</summary>
    /// <returns>The JWKS document. It is assembled rather than stored, so it always reflects the
    /// keys the verifier will actually accept.</returns>
    JwksDocument GetJwks();

    /// <summary>
    /// Generates a new key, makes it current and retires the previous one, keeping it acceptable
    /// for verification until the signatures it produced have expired.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the new key is durable and visible to every instance.</returns>
    ValueTask RotateAsync(CancellationToken ct);
}
