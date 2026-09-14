namespace Dle.Domain.Abuse;

/// <summary>
/// Lifecycle of an abuse report submitted through the public form (FR-245). The states exist
/// because the Digital Services Act, article 16, requires a traceable notice and action
/// mechanism, not merely a mailbox (§E.3).
/// </summary>
public enum AbuseReportStatus
{
    /// <summary>Submitted and not yet looked at. The four hour reaction target for critical
    /// reports starts here.</summary>
    New = 0,

    /// <summary>Read and classified by an operator.</summary>
    Triaged = 1,

    /// <summary>The abuse was confirmed; the link is quarantined and serves HTTP 410 (TC-103).</summary>
    Confirmed = 2,

    /// <summary>The report was examined and found to be unfounded. The link stays as it was.</summary>
    Rejected = 3,

    /// <summary>Handling is finished and the outcome is recorded in the resolution note.</summary>
    Resolved = 4,
}
