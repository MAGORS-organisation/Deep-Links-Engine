namespace Dle.Domain.Attribution;

/// <summary>
/// Strategy that produced an attribution (ADR-008). The order of the members follows the
/// strength of the evidence, not the order of evaluation.
/// </summary>
/// <remarks>
/// Every strategy except <see cref="Probabilistic"/> is deterministic and therefore carries a
/// confidence of exactly <c>1.00</c>. The distinction is never collapsed: an attribution record
/// always stores both the match type and the confidence (FR-186).
/// </remarks>
public enum MatchType
{
    /// <summary>No strategy matched. The install is reported as organic (TC-142).</summary>
    None = 0,

    /// <summary>Android Play Install Referrer carried the click identifier (S1, deterministic).</summary>
    InstallReferrer = 1,

    /// <summary>An anonymous web session was reconciled with a user account after login (S2, deterministic).</summary>
    Login = 2,

    /// <summary>The user typed the short code shown on the interstitial into the application (S3, deterministic).</summary>
    ClaimCode = 3,

    /// <summary>Device signals matched a recent click within the configured window (S4, never certain).</summary>
    Probabilistic = 4,

    /// <summary>
    /// The application was already installed and the universal or app link opened it directly;
    /// the SDK reported the URL back (S0). Deterministic, and the most frequent case of all
    /// — which is exactly why it is easy to forget (§B.6.4).
    /// </summary>
    DirectOpen = 5,
}
