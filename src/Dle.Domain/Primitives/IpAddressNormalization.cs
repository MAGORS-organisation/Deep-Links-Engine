using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Dle.Domain.Primitives;

/// <summary>
/// Normalizes IP addresses to a canonical representation before hashing, prefixing, or rate-limit
/// partitioning.
/// </summary>
public static class IpAddressNormalization
{
    /// <summary>
    /// Returns the canonical form of <paramref name="address"/>, unwrapping IPv4-mapped IPv6
    /// addresses (such as <c>::ffff:203.0.113.10</c>) to native IPv4 so both representations
    /// map to the same rate-limit bucket, prefix, and hash.
    /// </summary>
    /// <param name="address">The address to canonicalize, or <see langword="null"/>.</param>
    /// <returns>
    /// The canonical address, or <see langword="null"/> when <paramref name="address"/> is
    /// <see langword="null"/>.
    /// </returns>
    public static IPAddress? Canonicalize(IPAddress? address)
        => address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;

    /// <summary>
    /// Formats a rate-limit partition key from <paramref name="address"/>, canonicalizing
    /// IPv4-mapped IPv6 addresses to IPv4 and collapsing IPv6 addresses to their <c>/64</c> prefix.
    /// </summary>
    /// <param name="address">The remote address, or <see langword="null"/>.</param>
    /// <param name="unknownFallback">Key used when <paramref name="address"/> is <see langword="null"/>.</param>
    /// <returns>The partition key.</returns>
    public static string PartitionKey64(IPAddress? address, string unknownFallback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unknownFallback);

        IPAddress? canonical = Canonicalize(address);

        if (canonical is null)
        {
            return unknownFallback;
        }

        if (canonical.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return canonical.ToString();
        }

        Span<byte> bytes = stackalloc byte[16];

        if (!canonical.TryWriteBytes(bytes, out int written) || written != 16)
        {
            return canonical.ToString();
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Convert.ToHexStringLower(bytes[..8])}::/64");
    }
}
