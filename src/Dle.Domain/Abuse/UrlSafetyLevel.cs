namespace Dle.Domain.Abuse;

/// <summary>
/// How safe a target URL is judged to be (§E.3). The levels are ordered by severity so that a
/// caller aggregating several checkers can keep the worst verdict.
/// </summary>
public enum UrlSafetyLevel
{
    /// <summary>No checker could form an opinion, for example because a reputation feed was
    /// unavailable. Never treated as a pass by anything that can refuse.</summary>
    Unknown = 0,

    /// <summary>The URL passed every check that ran.</summary>
    Safe = 1,

    /// <summary>Something is off, but not conclusively. Worth flagging for review rather than
    /// refusing outright.</summary>
    Suspicious = 2,

    /// <summary>A reputation source positively identifies the target as phishing or malware.</summary>
    Malicious = 3,

    /// <summary>Refused by policy: a forbidden scheme, a private or link local address, or an
    /// operator blocklist entry. The distinction from <see cref="Malicious"/> matters because a
    /// policy refusal is our decision and is stable, while a reputation verdict is someone
    /// else's and can change.</summary>
    Blocked = 4,
}
