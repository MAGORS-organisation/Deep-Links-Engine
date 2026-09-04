namespace Dle.Domain.Clients;

/// <summary>
/// Coarse location derived from the remote address by the offline GeoIP database. Nothing finer
/// than a city is ever resolved, and the lookup never leaves the process (NFR-14).
/// </summary>
public sealed record GeoLocation
{
    /// <summary>ISO-3166-1 alpha-2 country code in upper case, for example <c>SK</c>.</summary>
    public string? Country { get; init; }

    /// <summary>Sub country region as named by the GeoIP database.</summary>
    public string? Region { get; init; }

    /// <summary>City name as named by the GeoIP database.</summary>
    public string? City { get; init; }
}
