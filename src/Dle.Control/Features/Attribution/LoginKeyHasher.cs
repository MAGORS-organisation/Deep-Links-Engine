using System.Security.Cryptography;
using System.Text;

using Dle.Crypto;

using Microsoft.Extensions.Options;

namespace Dle.Control.Features.Attribution;

/// <summary>
/// Turns the account identifier an SDK reports after sign-in into the keyed hash that login
/// reconciliation compares (S2, FR-185).
/// </summary>
/// <remarks>
/// <para>
/// The SDK already sends a hash rather than an account name — that is what
/// <see cref="Dle.Domain.Contracts.ResolveRequestDto.LoginKey"/> is — but a client-side hash is not
/// a control the server can rely on: the client chooses the algorithm, and an unkeyed hash of an
/// email address is reversible with a wordlist. Re-hashing here with a key that lives only in the
/// process means a database dump yields nothing linkable, and it means the click side and the
/// install side agree on one representation.
/// </para>
/// <para>
/// The key is HKDF-derived from the configured master secret under its own label, so recovering it
/// tells an attacker nothing about the slug permutation, the click identifier MAC or the claim code
/// pepper.
/// </para>
/// </remarks>
public sealed class LoginKeyHasher
{
    /// <summary>
    /// Key under which the edge records the hashed account identifier of a signed-in web visitor in
    /// <c>click_events.extra</c>. This is the contract between the two halves of S2.
    /// </summary>
    public const string ClickExtraKey = "login_key";

    private static readonly byte[] Salt = "dle:crypto:hkdf:v1"u8.ToArray();
    private static readonly byte[] Info = "dle:attribution:login-key:v1"u8.ToArray();

    private readonly byte[] _key;

    /// <summary>
    /// Creates the hasher.
    /// </summary>
    /// <param name="options">The crypto options, which carry the master secret.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">No master secret is configured.</exception>
    public LoginKeyHasher(IOptions<CryptoOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string master = options.Value.MasterSecret;

        if (string.IsNullOrWhiteSpace(master))
        {
            throw new InvalidOperationException(
                "Dle:Crypto:MasterSecret must be configured before login reconciliation can run.");
        }

        byte[] ikm = Encoding.UTF8.GetBytes(master);

        try
        {
            _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, Salt, Info);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ikm);
        }
    }

    /// <summary>Hashes a login key.</summary>
    /// <param name="loginKey">The value reported by the SDK. Untrusted; trimmed but not otherwise
    /// interpreted.</param>
    /// <returns>The 32 byte keyed hash stored in <c>installs.login_key_hash</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="loginKey"/> is empty.</exception>
    public byte[] Hash(string loginKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loginKey);

        byte[] message = Encoding.UTF8.GetBytes(loginKey.Trim());
        byte[] result = new byte[SHA256.HashSizeInBytes];

        HMACSHA256.HashData(_key, message, result);

        return result;
    }

    /// <summary>Renders a hash the way the click stream records it.</summary>
    /// <param name="hash">The hash.</param>
    /// <returns>Lowercase hexadecimal.</returns>
    public static string ToHex(byte[] hash)
    {
        ArgumentNullException.ThrowIfNull(hash);

        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Compares a hash against the value recorded with a click, in constant time
    /// (SHARED-KERNEL §17.6).
    /// </summary>
    /// <param name="hash">The hash computed from this request.</param>
    /// <param name="recorded">The value read from <c>click_events.extra</c>, or
    /// <see langword="null"/>.</param>
    /// <returns><see langword="true"/> only when the two denote the same account.</returns>
    public static bool Matches(byte[] hash, string? recorded)
    {
        ArgumentNullException.ThrowIfNull(hash);

        if (string.IsNullOrWhiteSpace(recorded))
        {
            return false;
        }

        byte[] candidate;

        try
        {
            candidate = Convert.FromHexString(recorded.Trim());
        }
        catch (FormatException)
        {
            // Explicit default: a value that is not hexadecimal is not a match. The click stream is
            // written by another component, so a malformed entry has to fail closed rather than
            // throw on the attribution path (SHARED-KERNEL §17.9).
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(hash, candidate);
    }
}
