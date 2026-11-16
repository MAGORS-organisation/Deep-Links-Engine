using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Dle.Domain.Primitives;

/// <summary>
/// The peers a service accepts <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> from.
/// </summary>
/// <remarks>
/// ASP.NET Core's forwarded headers middleware trusts <em>every</em> peer once both of its lists are
/// empty, so "no proxy was named" must never reach it as two empty lists: any client could then pick
/// its own address, and with it its own rate limit bucket, country and hashed identifier. Parsing
/// here therefore fails when nothing is named and when an entry does not parse, so the mistake is a
/// refusal to start rather than a silently widened trust boundary.
/// </remarks>
/// <param name="Addresses">Individual proxy addresses.</param>
/// <param name="Networks">Proxy networks.</param>
public sealed record TrustedProxies(IReadOnlyList<IPAddress> Addresses, IReadOnlyList<IPNetwork> Networks)
{
    /// <summary>
    /// Parses the configured proxy addresses and CIDR networks.
    /// </summary>
    /// <param name="knownProxies">Addresses, for example <c>10.0.0.5</c>. Blank entries are ignored.</param>
    /// <param name="knownNetworks">Networks in CIDR notation, for example <c>10.0.0.0/8</c>. Blank entries are ignored.</param>
    /// <param name="trusted">The parsed set when the method returns <see langword="true"/>.</param>
    /// <returns>
    /// <see langword="false"/> when an entry does not parse or when no entry is left, which are the two
    /// states that must not be handed to the middleware.
    /// </returns>
    public static bool TryParse(
        IEnumerable<string>? knownProxies,
        IEnumerable<string>? knownNetworks,
        [NotNullWhen(true)] out TrustedProxies? trusted)
    {
        trusted = null;

        List<IPAddress> addresses = [];
        List<IPNetwork> networks = [];

        foreach (string candidate in knownProxies ?? [])
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (!IPAddress.TryParse(candidate.Trim(), out IPAddress? address))
            {
                return false;
            }

            addresses.Add(address);
        }

        foreach (string candidate in knownNetworks ?? [])
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (!IPNetwork.TryParse(candidate.Trim(), out IPNetwork network))
            {
                return false;
            }

            networks.Add(network);
        }

        if (addresses.Count == 0 && networks.Count == 0)
        {
            return false;
        }

        trusted = new TrustedProxies(addresses, networks);
        return true;
    }
}
