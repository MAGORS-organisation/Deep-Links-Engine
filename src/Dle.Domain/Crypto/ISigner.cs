namespace Dle.Domain.Crypto;

/// <summary>
/// Produces signatures with exactly one algorithm and one key. A signer is deliberately
/// single valued while <see cref="IVerifier"/> accepts a set: that asymmetry is what makes key
/// and algorithm rotation possible without an outage (§E.4.2).
/// </summary>
public interface ISigner
{
    /// <summary>Identifier of the algorithm, one of the constants on
    /// <see cref="SignatureAlgorithms"/>. Written into the token.</summary>
    string AlgorithmId { get; }

    /// <summary>Identifier of the signing key, written into the token so that a verifier can
    /// pick the right public key during a rotation.</summary>
    string KeyId { get; }

    /// <summary>Upper bound on the size of a produced signature in bytes. Lets callers size a
    /// buffer without knowing the algorithm; post-quantum signatures are large enough that
    /// guessing is not an option.</summary>
    int MaxSignatureSize { get; }

    /// <summary>Signs a payload.</summary>
    /// <param name="payload">The exact bytes to sign.</param>
    /// <returns>The raw signature bytes, not encoded.</returns>
    byte[] Sign(ReadOnlySpan<byte> payload);
}
