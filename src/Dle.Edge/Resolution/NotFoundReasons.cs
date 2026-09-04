namespace Dle.Edge.Resolution;

/// <summary>
/// Why a resolve produced no link. Diagnostic only.
/// </summary>
/// <remarks>
/// The distinction exists in the log at <c>Debug</c> and nowhere else. It never reaches the response:
/// the status code, the headers, the body and the work done to produce them are identical for every
/// reason below, because a client able to tell "no such link" from "not your link" can enumerate a
/// competitor's campaigns (T-07, TC-102, TC-166).
/// </remarks>
public static class NotFoundReasons
{
    /// <summary>The <c>Host</c> header could not be normalized.</summary>
    public const string UnusableHost = "unusable_host";

    /// <summary>The path segment could not be normalized into a slug, or names a reserved word (TC-109).</summary>
    public const string UnusableSlug = "unusable_slug";

    /// <summary>No link exists for this host and slug, or it belongs to a host this request did not name.</summary>
    public const string NoSuchLink = "no_such_link";

    /// <summary>The link exists but is not active.</summary>
    public const string Inactive = "inactive";

    /// <summary>The link's scheduled window has not opened yet.</summary>
    public const string NotYetActive = "not_yet_active";

    /// <summary>The link's window has closed and it has no expired target configured.</summary>
    public const string Expired = "expired";

    /// <summary>The network prefix is in the enumeration shadow ban (§E.9, T-07).</summary>
    public const string ShadowBanned = "shadow_banned";

    /// <summary>No routing rule matched and the rule set has no default (FR-127).</summary>
    public const string NoMatchingRule = "no_matching_rule";
}
