namespace Dle.Domain.Privacy;

/// <summary>
/// Operating mode of the tenant with respect to ePrivacy article 5(3) and the EDPB
/// guidelines 2/2023 (specification §E.6.2). The values are ordered from the most to the least
/// restrictive, which is what makes <c>min()</c> the correct way to combine a tenant mode with a
/// domain override.
/// </summary>
public enum ConsentMode
{
    /// <summary>
    /// No identifier is stored at all. Only the click counter of the link is incremented:
    /// no IP address in any form, no user agent detail, no cross session linking.
    /// </summary>
    Off = 0,

    /// <summary>
    /// Default. The IP address is hashed immediately with the daily rotated salt and never stored
    /// raw; country, device class and OS family are kept. No cross session linking, no attribution
    /// on the level of an individual.
    /// </summary>
    AggregateOnly = 1,

    /// <summary>
    /// Click identifier linking, deferred matching, the probabilistic module and the truncated IP
    /// prefix. Legal basis is consent, so this mode only takes effect when the request carries a
    /// documented attribution consent signal.
    /// </summary>
    Full = 2,
}
