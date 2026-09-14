using System.Buffers.Binary;
using System.Text;

using Dle.Domain.Attribution;

namespace Dle.Crypto;

/// <summary>
/// Issues and checks claim codes: the six characters a user reads off the interstitial page and
/// types into the freshly installed application (S3, FR-184, K3).
/// </summary>
/// <remarks>
/// <para>
/// The code is drawn from 30 bits of randomness, which the six character alphabet then caps at
/// 26^6, about 2^28.2. That is not a credential and is not treated as one: §E.4.1 pairs it with a
/// one hour time to live and a rate limit, and <c>claim_codes.consumed_at</c> makes it single use.
/// The honest framing is a claim ticket that is cheap to guess once and pointless to guess twice.
/// </para>
/// <para>
/// Storage is a keyed hash, not a plain one. A plain SHA-256 of a 28 bit code is brute forceable
/// in seconds on a laptop, so a leaked table would hand over every outstanding claim. The HMAC key
/// lives in the process, never in the database, which means an attacker needs both to get
/// anywhere.
/// </para>
/// </remarks>
public sealed class ClaimCodeGenerator
{
    /// <summary>Size of the code space: the alphabet raised to the code length.</summary>
    private const uint Modulus = 26 * 26 * 26 * 26 * 26 * 26;

    /// <summary>Bits of randomness drawn per attempt.</summary>
    private const int EntropyBits = 30;

    /// <summary>Largest draw that can be reduced without bias.</summary>
    private const uint RejectionLimit = ((1u << EntropyBits) / Modulus) * Modulus;

    private readonly byte[] _key;

    /// <summary>
    /// Creates a generator.
    /// </summary>
    /// <param name="secret">The pepper the code hash is keyed with.</param>
    /// <exception cref="ArgumentException"><paramref name="secret"/> is shorter than 16 bytes.</exception>
    public ClaimCodeGenerator(ReadOnlySpan<byte> secret)
    {
        if (secret.Length < 16)
        {
            throw new ArgumentException("A claim code secret must be at least 16 bytes.", nameof(secret));
        }

        _key = secret.ToArray();
    }

    /// <summary>
    /// Issues a code.
    /// </summary>
    /// <returns>The code and the hash to store. The code itself is never persisted.</returns>
    public ClaimCodeCredential Create()
    {
        string code = NewCode();

        return new ClaimCodeCredential
        {
            Code = code,
            Hash = HashOf(code),
        };
    }

    /// <summary>
    /// Hashes a code for storage or lookup.
    /// </summary>
    /// <param name="code">The already normalized code, as produced by
    /// <see cref="ClaimCode.Normalize"/>.</param>
    /// <returns>The keyed hash.</returns>
    /// <exception cref="ArgumentException"><paramref name="code"/> is <see langword="null"/> or empty.</exception>
    public byte[] HashOf(string code)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);

        byte[] message = Encoding.UTF8.GetBytes(code);
        byte[] result = new byte[SHA256.HashSizeInBytes];

        HMACSHA256.HashData(_key, message, result);

        return result;
    }

    /// <summary>
    /// Checks a code a user typed against a stored hash, normalizing it first.
    /// </summary>
    /// <param name="presentedCode">The code as typed. Untrusted.</param>
    /// <param name="storedHash">The <c>claim_codes.code_hash</c> value.</param>
    /// <returns><see langword="true"/> only when the code matches. The comparison is constant
    /// time (T-17, S-03).</returns>
    public bool Verify(string? presentedCode, ReadOnlySpan<byte> storedHash)
    {
        if (string.IsNullOrEmpty(presentedCode) || storedHash.IsEmpty)
        {
            return false;
        }

        string normalized = ClaimCode.Normalize(presentedCode);

        if (!ClaimCode.IsWellFormed(normalized))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(HashOf(normalized), storedHash);
    }

    /// <summary>
    /// Draws a code. Rejection sampling rather than a plain modulo: 2^30 is not a multiple of
    /// 26^6, so reducing every draw would make the low codes measurably more likely, and a
    /// non uniform 28 bit space is even easier to guess than a uniform one.
    /// </summary>
    private static string NewCode()
    {
        Span<char> code = stackalloc char[ClaimCode.Length];
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        uint value;

        do
        {
            RandomNumberGenerator.Fill(buffer);
            value = BinaryPrimitives.ReadUInt32BigEndian(buffer) >> (32 - EntropyBits);
        }
        while (value >= RejectionLimit);

        value %= Modulus;

        for (int i = ClaimCode.Length - 1; i >= 0; i--)
        {
            code[i] = ClaimCode.Alphabet[(int)(value % (uint)ClaimCode.Alphabet.Length)];
            value /= (uint)ClaimCode.Alphabet.Length;
        }

        return new string(code);
    }
}
