using System.Net;

namespace Dle.Edge.Clients;

/// <summary>
/// The geographic resolver used when <c>Dle:Edge:GeoIp:Provider</c> is <c>None</c>: every lookup
/// answers "unknown".
/// </summary>
/// <remarks>
/// <para>
/// A deployment that does not ship a GeoLite2 file is a supported configuration, not a broken one.
/// Country stays <see langword="null"/>, a routing rule with a <c>country</c> condition simply never
/// matches, and evaluation falls through to the default rule that FR-127 guarantees exists. That is
/// the same observable behaviour as a missing database file (§D.6), reached deliberately rather than
/// by accident.
/// </para>
/// <para>
/// It exists as its own type instead of a flag inside
/// <see cref="MaxMindGeoIpResolver"/> so that the composition root states the choice once, and so that
/// nothing on the resolve path branches on configuration per request.
/// </para>
/// </remarks>
public sealed class NullGeoIpResolver : IGeoIpResolver
{
    /// <summary>The single instance; the type carries no state.</summary>
    public static NullGeoIpResolver Instance { get; } = new();

    /// <inheritdoc />
    /// <remarks>Always <see langword="false"/>: there is no database to consult.</remarks>
    public bool IsAvailable => false;

    /// <inheritdoc />
    /// <remarks>Always <see langword="null"/>.</remarks>
    public GeoLocation? Resolve(IPAddress? address) => null;
}
