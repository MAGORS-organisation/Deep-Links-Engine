using System.Text;

using Microsoft.Extensions.Options;

namespace Dle.Crypto;

/// <summary>
/// Mints and verifies control plane API keys (FR-242, K5 of the cryptographic inventory).
/// </summary>
/// <remarks>
/// <para>
/// A key looks like <c>dle_a1B2c3D4_&lt;43 base62 characters&gt;</c>. The middle field is the
/// non secret prefix: it is stored in clear text so a presented key can be found with one indexed
/// lookup rather than by hashing every row, and it is what an operator recognises in a list. The
/// last field carries 256 bits of entropy, which is why the key itself never needs to be
/// memorable or rotated on a schedule.
/// </para>
/// <para>
/// Only the Argon2id hash is stored, so a database dump yields no working credentials. The
/// verification is deliberately expensive, which means the control plane has to cache a successful
/// result for a short window rather than paying it on every request.
/// </para>
/// </remarks>
public sealed class ApiKeyHasher
{
    /// <summary>Separator between the three fields of a key.</summary>
    public const char FieldSeparator = '_';

    /// <summary>Number of characters in the public prefix field.</summary>
    public const int PrefixLength = 8;

    /// <summary>Characters in the secret field: 62^43 is just above 2^256.</summary>
    private const int SecretLength = 43;

    private readonly Argon2PasswordHasher _hasher;
    private readonly string _namePrefix;

    /// <summary>
    /// Creates a hasher.
    /// </summary>
    /// <param name="hasher">The Argon2id hasher.</param>
    /// <param name="options">The crypto options, which carry the key name prefix.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public ApiKeyHasher(Argon2PasswordHasher hasher, IOptions<CryptoOptions> options)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(options);

        _hasher = hasher;
        _namePrefix = options.Value.ApiKeyPrefix;
    }

    /// <summary>
    /// Mints a key.
    /// </summary>
    /// <returns>The credential. The plaintext value is present only on this object and is never
    /// recoverable afterwards.</returns>
    public ApiKeyCredential Create()
    {
        string prefix = RandomBase62(PrefixLength);
        string secret = RandomBase62(SecretLength);
        string token = _namePrefix + FieldSeparator + prefix + FieldSeparator + secret;

        return new ApiKeyCredential
        {
            Token = token,
            Prefix = prefix,
            Hash = Encoding.UTF8.GetBytes(_hasher.Hash(token)),
        };
    }

    /// <summary>
    /// Verifies a presented key against a stored hash in constant time.
    /// </summary>
    /// <param name="presentedKey">The key as received. Untrusted.</param>
    /// <param name="storedHash">The <c>api_keys.hash</c> value.</param>
    /// <returns><see langword="true"/> only when the key matches.</returns>
    public static bool Verify(string? presentedKey, ReadOnlySpan<byte> storedHash)
    {
        if (string.IsNullOrEmpty(presentedKey) || storedHash.IsEmpty)
        {
            return false;
        }

        string encoded = Encoding.UTF8.GetString(storedHash);

        return Argon2PasswordHasher.Verify(presentedKey, encoded);
    }

    /// <summary>
    /// Extracts the public prefix of a presented key, so the caller can find the candidate row.
    /// </summary>
    /// <param name="presentedKey">The key as received. Untrusted.</param>
    /// <param name="prefix">The prefix on success, empty otherwise.</param>
    /// <returns><see langword="false"/> when the value does not have the shape of a key. The shape
    /// check is not authentication: a well formed key still has to pass
    /// <see cref="Verify"/>.</returns>
    public static bool TryReadPrefix(string? presentedKey, out string prefix)
    {
        prefix = string.Empty;

        if (string.IsNullOrEmpty(presentedKey))
        {
            return false;
        }

        string[] fields = presentedKey.Split(FieldSeparator);

        if (fields.Length != 3 || fields[1].Length != PrefixLength || fields[2].Length != SecretLength)
        {
            return false;
        }

        foreach (char c in fields[1])
        {
            if (!Base62.Alphabet.Contains(c, StringComparison.Ordinal))
            {
                return false;
            }
        }

        prefix = fields[1];
        return true;
    }

    /// <summary>
    /// Draws characters uniformly from the base62 alphabet. Rejection sampling rather than a
    /// modulo of a random byte: 256 is not a multiple of 62, so a modulo would make the first four
    /// characters of the alphabet measurably more likely.
    /// </summary>
    private static string RandomBase62(int length)
    {
        const int limit = 256 - (256 % 62);

        Span<char> result = stackalloc char[length];
        Span<byte> buffer = stackalloc byte[1];
        int written = 0;

        while (written < length)
        {
            RandomNumberGenerator.Fill(buffer);

            if (buffer[0] < limit)
            {
                result[written++] = Base62.Alphabet[buffer[0] % 62];
            }
        }

        return new string(result);
    }
}
