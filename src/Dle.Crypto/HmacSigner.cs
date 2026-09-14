namespace Dle.Crypto;

/// <summary>
/// HMAC-SHA-256 signer (<see cref="SignatureAlgorithms.Hs256"/>), the default for click tokens and
/// the <c>v1</c> webhook slot (K2, K4).
/// </summary>
/// <remarks>
/// Symmetric and therefore free of post-quantum exposure: 256 bits of key leave 128 after Grover,
/// which is why §E.5.4 places the migration pressure on the asymmetric signatures instead. The
/// trade-off is that a verifier needs the same secret, so a third party integrator who must verify
/// without holding it gets Ed25519.
/// </remarks>
public sealed class HmacSigner : ISigner
{
    private readonly byte[] _key;

    /// <summary>
    /// Creates a signer.
    /// </summary>
    /// <param name="keyId">Identifier written into the token.</param>
    /// <param name="key">The HMAC key, at least 32 bytes.</param>
    /// <exception cref="ArgumentException"><paramref name="keyId"/> is empty or
    /// <paramref name="key"/> is shorter than 32 bytes.</exception>
    public HmacSigner(string keyId, ReadOnlySpan<byte> key)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyId);

        if (key.Length < 32)
        {
            throw new ArgumentException("An HS256 key must be at least 32 bytes.", nameof(key));
        }

        KeyId = keyId;
        _key = key.ToArray();
    }

    /// <inheritdoc />
    public string AlgorithmId => SignatureAlgorithms.Hs256;

    /// <inheritdoc />
    public string KeyId { get; }

    /// <inheritdoc />
    public int MaxSignatureSize => SHA256.HashSizeInBytes;

    /// <inheritdoc />
    public byte[] Sign(ReadOnlySpan<byte> payload) => HMACSHA256.HashData(_key, payload);
}
