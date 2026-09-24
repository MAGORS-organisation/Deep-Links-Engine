using System.Net;

namespace Dle.Domain.Crypto;

/// <summary>
/// Turns a remote address into the only two forms the engine is ever allowed to store: a keyed
/// hash and a truncated network prefix (§B.5.3, FR-247, K9 of the cryptographic inventory).
/// </summary>
/// <remarks>
/// An IP address is personal data (Breyer, C-582/14), so the raw address never reaches storage.
/// The salt rotates daily, which bounds how long two events can be correlated through the hash;
/// this is what makes the daily rotation a privacy control rather than key hygiene. The prefix is
/// coarser still and is written only under full consent.
/// </remarks>
public interface IIpHasher
{
    /// <summary>Computes the keyed hash of an address.</summary>
    /// <param name="address">The remote address.</param>
    /// <param name="when">Instant of the event, which selects the daily salt.</param>
    /// <returns>The hash bytes stored in <c>click_events.ip_hash</c>.</returns>
    byte[] Hash(IPAddress address, DateTimeOffset when);

    /// <summary>Truncates an address to its network prefix.</summary>
    /// <param name="address">The remote address.</param>
    /// <returns>The prefix, /24 for IPv4 and /48 for IPv6, or <see langword="null"/> when the
    /// address family is not one the engine handles.</returns>
    string? Prefix(IPAddress address);
}
