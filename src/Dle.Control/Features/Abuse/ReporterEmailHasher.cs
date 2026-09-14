using System.Security.Cryptography;
using System.Text;

using Dle.Crypto;

using Microsoft.Extensions.Options;

namespace Dle.Control.Features.Abuse;

/// <summary>
/// Turns a reporter's address into the hash that is stored instead of it (§E.6.3).
/// </summary>
/// <remarks>
/// <para>
/// An operator needs two things from a reporter's address: to recognise the same person reporting
/// again, and to deduplicate a flood. A keyed hash answers both. A column holding addresses answers
/// them too, and additionally builds a list of people who reported someone — which is a liability
/// with no operational value, and is exactly the sort of data an abuse pipeline attracts subpoenas
/// for.
/// </para>
/// <para>
/// The key is derived from the instance master secret, so the hash is stable across restarts (the
/// operator can still match a repeat reporter next month) and useless to anyone who obtains only
/// the database. It is HMAC rather than a plain digest for the same reason: the address space of
/// email addresses is small enough to enumerate against an unkeyed hash.
/// </para>
/// </remarks>
public sealed class ReporterEmailHasher
{
    /// <summary>HKDF label, so this key is not the key of any other purpose.</summary>
    private static readonly byte[] Info = "dle:abuse:reporter-email:v1"u8.ToArray();

    /// <summary>Fixed salt. A constant salt is sound for HKDF; the entropy is in the secret.</summary>
    private static readonly byte[] Salt = "dle:crypto:hkdf:v1"u8.ToArray();

    private readonly byte[] _key;

    /// <summary>Creates the hasher.</summary>
    /// <param name="options">Crypto options, read for the master secret.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public ReporterEmailHasher(IOptions<CryptoOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _key = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            Encoding.UTF8.GetBytes(options.Value.MasterSecret),
            outputLength: 32,
            salt: Salt,
            info: Info);
    }

    /// <summary>Hashes an address.</summary>
    /// <param name="email">The submitted address, or <see langword="null"/> when the reporter
    /// chose not to leave one.</param>
    /// <returns>The hash, or <see langword="null"/> when there was no address to hash.</returns>
    /// <remarks>
    /// The address is trimmed and lowercased before hashing, so the same person writing
    /// <c>Someone@Example.COM</c> and <c>someone@example.com</c> is recognised as the same reporter.
    /// No further canonicalisation is attempted: stripping dots or plus tags is provider specific
    /// folklore, and getting it wrong silently merges two people into one.
    /// </remarks>
    public byte[]? Hash(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        byte[] normalized = Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant());
        return HMACSHA256.HashData(_key, normalized);
    }
}
