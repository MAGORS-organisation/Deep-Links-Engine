using System.Net;
using Dle.Domain.Clients;

namespace Dle.Domain.Ports;

/// <summary>
/// Resolves a remote address to a coarse location for country and region routing (FR-122).
/// </summary>
/// <remarks>
/// The lookup is offline against a local database. No outbound call to a third party is permitted
/// on the resolve path, both because of the latency budget and because sending every visitor
/// address to an external service would undo the data minimisation the rest of the design is
/// built on (NFR-14, §E.6.3).
/// </remarks>
public interface IGeoIpResolver
{
    /// <summary>Resolves an address.</summary>
    /// <param name="address">The remote address, possibly <see langword="null"/>.</param>
    /// <returns>The location, or <see langword="null"/> when the address is unknown or the
    /// database is not installed.</returns>
    GeoLocation? Resolve(IPAddress? address);

    /// <summary>Whether a database is loaded. When it is not, country rules never match and the
    /// default rule takes over; that is reported as degraded rather than failing the request.</summary>
    bool IsAvailable { get; }
}
