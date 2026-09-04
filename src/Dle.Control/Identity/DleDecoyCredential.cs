using System.Security.Cryptography;
using System.Text;

using Dle.Crypto;

namespace Dle.Control.Identity;

/// <summary>
/// A hash of a value nobody knows, verified against when the presented key matches no row.
/// </summary>
/// <remarks>
/// <para>
/// Without it, an unknown key prefix would be refused after one indexed lookup while a known one
/// would be refused after a full Argon2id verification. The difference is tens of milliseconds and
/// is trivially measurable over a few samples, which turns the endpoint into an oracle telling an
/// attacker exactly which prefixes exist — the enumeration T-07 is about.
/// </para>
/// <para>
/// The secret is random per process and never leaves it, so the decoy verification can only ever
/// fail; what it buys is that it fails after doing the same work as a real one.
/// </para>
/// </remarks>
public sealed class DleDecoyCredential
{
    /// <summary>Builds the decoy under the configured Argon2id cost parameters.</summary>
    /// <param name="hasher">The hasher whose parameters real keys are stored under.</param>
    /// <exception cref="ArgumentNullException"><paramref name="hasher"/> is <see langword="null"/>.</exception>
    public DleDecoyCredential(Argon2PasswordHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);

        string secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        Hash = Encoding.UTF8.GetBytes(hasher.Hash(secret));
    }

    /// <summary>The encoded hash to verify against when there is no candidate row.</summary>
    public byte[] Hash { get; }
}
