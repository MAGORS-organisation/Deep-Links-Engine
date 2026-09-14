using System.Text;

using Konscious.Security.Cryptography;

namespace Dle.Crypto;

/// <summary>
/// Argon2id hashing in the PHC string format, used for API keys (K5).
/// </summary>
/// <remarks>
/// <para>
/// The cost parameters travel inside the encoded hash rather than only in configuration. That is
/// what makes raising them safe: an old hash keeps verifying under the parameters it was made
/// with, and a caller can notice that a stored hash is below the current policy and rehash on the
/// next successful verification. Storing the parameters only in configuration would make every
/// existing hash unverifiable the moment the policy changed.
/// </para>
/// <para>
/// Comparison goes through <see cref="CryptographicOperations.FixedTimeEquals"/>, without
/// exception (T-17, S-03, SHARED-KERNEL §17.6).
/// </para>
/// </remarks>
public sealed class Argon2PasswordHasher
{
    /// <summary>Argon2 version marker written into the encoded form: 0x13 is version 1.3.</summary>
    private const int Version = 19;

    /// <summary>Number of dollar separated fields in a PHC encoded Argon2 hash.</summary>
    private const int EncodedFieldCount = 6;

    private readonly Argon2Options _options;

    /// <summary>
    /// Creates a hasher.
    /// </summary>
    /// <param name="options">Cost parameters used for new hashes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public Argon2PasswordHasher(Argon2Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// Hashes a secret under the configured cost parameters.
    /// </summary>
    /// <param name="secret">The secret to hash.</param>
    /// <returns>The PHC encoded hash, for example
    /// <c>$argon2id$v=19$m=19456,t=2,p=1$&lt;salt&gt;$&lt;hash&gt;</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="secret"/> is <see langword="null"/> or empty.</exception>
    public string Hash(string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);

        byte[] salt = RandomNumberGenerator.GetBytes(_options.SaltLength);
        byte[] hash = Derive(secret, salt, _options.MemoryKib, _options.Iterations, _options.Parallelism, _options.HashLength);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"$argon2id$v={Version}$m={_options.MemoryKib},t={_options.Iterations},p={_options.Parallelism}${ToBase64(salt)}${ToBase64(hash)}");
    }

    /// <summary>
    /// Verifies a secret against an encoded hash. Never throws: the encoded value may come from a
    /// database row that predates a schema change, and a malformed row must read as "does not
    /// match" rather than as a server error.
    /// </summary>
    /// <param name="secret">The secret to check.</param>
    /// <param name="encoded">The PHC encoded hash.</param>
    /// <returns><see langword="true"/> only when the secret produces the stored hash.</returns>
    public static bool Verify(string? secret, string? encoded)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(encoded))
        {
            return false;
        }

        string[] fields = encoded.Split('$');

        // The leading empty field is the one before the first separator.
        if (fields.Length != EncodedFieldCount || !string.Equals(fields[1], "argon2id", StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryParseCost(fields[3], out int memoryKib, out int iterations, out int parallelism))
        {
            return false;
        }

        if (!TryFromBase64(fields[4], out byte[] salt) || !TryFromBase64(fields[5], out byte[] expected))
        {
            return false;
        }

        byte[] actual = Derive(secret, salt, memoryKib, iterations, parallelism, expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// Whether a stored hash was produced with weaker parameters than the current policy, so the
    /// caller can rehash it after a successful verification.
    /// </summary>
    /// <param name="encoded">The PHC encoded hash.</param>
    /// <returns><see langword="true"/> when the hash should be replaced. An unparseable value also
    /// returns <see langword="true"/>: it is not a hash this policy would produce.</returns>
    public bool NeedsRehash(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded))
        {
            return true;
        }

        string[] fields = encoded.Split('$');

        if (fields.Length != EncodedFieldCount ||
            !string.Equals(fields[1], "argon2id", StringComparison.Ordinal) ||
            !TryParseCost(fields[3], out int memoryKib, out int iterations, out int parallelism))
        {
            return true;
        }

        return memoryKib < _options.MemoryKib ||
               iterations < _options.Iterations ||
               parallelism != _options.Parallelism;
    }

    private static byte[] Derive(string secret, byte[] salt, int memoryKib, int iterations, int parallelism, int length)
    {
        byte[] password = Encoding.UTF8.GetBytes(secret);

        try
        {
            using Argon2id argon2 = new(password)
            {
                Salt = salt,
                MemorySize = memoryKib,
                Iterations = iterations,
                DegreeOfParallelism = parallelism,
            };

            return argon2.GetBytes(length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
        }
    }

    private static bool TryParseCost(string field, out int memoryKib, out int iterations, out int parallelism)
    {
        memoryKib = 0;
        iterations = 0;
        parallelism = 0;

        string[] parts = field.Split(',');

        if (parts.Length != 3)
        {
            return false;
        }

        return TryParseLabelled(parts[0], "m=", out memoryKib)
            && TryParseLabelled(parts[1], "t=", out iterations)
            && TryParseLabelled(parts[2], "p=", out parallelism);
    }

    private static bool TryParseLabelled(string part, string label, out int value)
    {
        value = 0;

        if (!part.StartsWith(label, StringComparison.Ordinal))
        {
            return false;
        }

        return int.TryParse(
            part.AsSpan(label.Length),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out value) && value > 0;
    }

    /// <summary>Base64 without padding, which is what the PHC format specifies.</summary>
    private static string ToBase64(byte[] value) => Convert.ToBase64String(value).TrimEnd('=');

    private static bool TryFromBase64(string value, out byte[] decoded)
    {
        int padding = (4 - (value.Length % 4)) % 4;
        string padded = padding == 0 ? value : value + new string('=', padding);
        byte[] buffer = new byte[((padded.Length + 3) / 4) * 3];

        if (!Convert.TryFromBase64String(padded, buffer, out int written) || written == 0)
        {
            decoded = [];
            return false;
        }

        decoded = buffer[..written];
        return true;
    }
}
