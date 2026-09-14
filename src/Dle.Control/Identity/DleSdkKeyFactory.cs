using System.Security.Cryptography;
using System.Text;

using Dle.Control.Configuration;
using Dle.Crypto;

using Microsoft.Extensions.Options;

namespace Dle.Control.Identity;

/// <summary>
/// Mints keys for customer applications, in the same shape as a control-plane key but under a
/// different name field (§E.2.1, TB2).
/// </summary>
/// <remarks>
/// The two credentials look alike on the wire on purpose — the same three fields, the same prefix
/// lookup, the same Argon2id verification — because there is no reason for the weaker one to have
/// weaker cryptography. What differs is the name field, which routes the value to the SDK
/// authentication scheme, and therefore to the one policy that cannot write configuration.
/// </remarks>
public sealed class DleSdkKeyFactory
{
    /// <summary>Number of characters in the public prefix field.</summary>
    public const int PrefixLength = ApiKeyHasher.PrefixLength;

    /// <summary>Characters in the secret field: 62^43 is just above 2^256.</summary>
    private const int SecretLength = 43;

    private readonly Argon2PasswordHasher _hasher;
    private readonly string _keyName;

    /// <summary>Creates the factory.</summary>
    /// <param name="hasher">The Argon2id hasher.</param>
    /// <param name="options">Identity options carrying the SDK key name field.</param>
    public DleSdkKeyFactory(Argon2PasswordHasher hasher, IOptions<DleIdentityOptions> options)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(options);

        _hasher = hasher;
        _keyName = options.Value.SdkKeyName;
    }

    /// <summary>Mints a key.</summary>
    /// <returns>The plaintext value, its public prefix, and the hash to store.</returns>
    public (string Token, string Prefix, byte[] Hash) Create()
    {
        string prefix = RandomBase62(PrefixLength);
        string secret = RandomBase62(SecretLength);
        string token = string.Concat(
            _keyName,
            ApiKeyHasher.FieldSeparator,
            prefix,
            ApiKeyHasher.FieldSeparator,
            secret);

        return (token, prefix, Encoding.UTF8.GetBytes(_hasher.Hash(token)));
    }

    /// <summary>
    /// Draws characters uniformly from the base62 alphabet by rejection sampling.
    /// </summary>
    /// <param name="length">How many characters to draw.</param>
    /// <returns>The drawn characters.</returns>
    /// <remarks>
    /// 256 is not a multiple of 62, so taking a random byte modulo 62 would make the first four
    /// characters of the alphabet measurably more likely. Rejection sampling costs a few extra bytes
    /// of entropy and removes the bias.
    /// </remarks>
    private static string RandomBase62(int length)
    {
        const int Limit = 256 - (256 % 62);

        Span<char> result = stackalloc char[length];
        Span<byte> buffer = stackalloc byte[1];
        int written = 0;

        while (written < length)
        {
            RandomNumberGenerator.Fill(buffer);

            if (buffer[0] < Limit)
            {
                result[written++] = Base62.Alphabet[buffer[0] % 62];
            }
        }

        return new string(result);
    }
}
