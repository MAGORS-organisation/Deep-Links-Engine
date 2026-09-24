namespace Dle.Domain.Attribution;

/// <summary>
/// Canonical values of the <c>attributions.match_type</c> column (§B.5.3). The storage and wire
/// representation of <see cref="MatchType"/> is a stable lowercase string, so the column stays
/// readable in the database and survives renumbering of the enumeration.
/// </summary>
public static class MatchTypeNames
{
    /// <summary>No strategy matched.</summary>
    public const string None = "none";

    /// <summary>The Android Play Install Referrer carried the click identifier.</summary>
    public const string InstallReferrer = "install_referrer";

    /// <summary>Reconciliation of an anonymous session with a user account after login.</summary>
    public const string Login = "login";

    /// <summary>A short claim code entered by the user inside the application.</summary>
    public const string ClaimCode = "claim_code";

    /// <summary>Device signals matched a recent click inside the configured window.</summary>
    public const string Probabilistic = "probabilistic";

    /// <summary>The link opened an already installed application directly.</summary>
    public const string DirectOpen = "direct_open";

    /// <summary>Maps a match type to its stored name.</summary>
    /// <param name="t">The match type to convert.</param>
    /// <returns>The stable lowercase name. A value outside the enumeration maps to
    /// <see cref="None"/>: an unrecognised strategy must never be reported as a match.</returns>
    public static string From(MatchType t) => t switch
    {
        MatchType.InstallReferrer => InstallReferrer,
        MatchType.Login => Login,
        MatchType.ClaimCode => ClaimCode,
        MatchType.Probabilistic => Probabilistic,
        MatchType.DirectOpen => DirectOpen,
        _ => None,
    };

    /// <summary>Parses a stored match type name.</summary>
    /// <param name="s">The name as read from the database or from a request payload. Matching is
    /// case insensitive and surrounding whitespace is ignored.</param>
    /// <returns>The corresponding match type. <see langword="null"/>, empty and unknown input all
    /// map to <see cref="MatchType.None"/> — the parser fails closed.</returns>
    public static MatchType Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return MatchType.None;
        }

        string value = s.Trim();

        if (string.Equals(value, InstallReferrer, StringComparison.OrdinalIgnoreCase))
        {
            return MatchType.InstallReferrer;
        }

        if (string.Equals(value, Login, StringComparison.OrdinalIgnoreCase))
        {
            return MatchType.Login;
        }

        if (string.Equals(value, ClaimCode, StringComparison.OrdinalIgnoreCase))
        {
            return MatchType.ClaimCode;
        }

        if (string.Equals(value, Probabilistic, StringComparison.OrdinalIgnoreCase))
        {
            return MatchType.Probabilistic;
        }

        if (string.Equals(value, DirectOpen, StringComparison.OrdinalIgnoreCase))
        {
            return MatchType.DirectOpen;
        }

        return MatchType.None;
    }
}
