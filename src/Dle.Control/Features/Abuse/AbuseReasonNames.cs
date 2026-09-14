using Dle.Domain.Abuse;

namespace Dle.Control.Features.Abuse;

/// <summary>
/// The wire spellings of <see cref="AbuseReason"/> and <see cref="AbuseReportStatus"/>.
/// </summary>
/// <remarks>
/// The columns hold text rather than an integer (SHARED-KERNEL §12), so these strings are both the
/// API contract and the stored value. They are written once, here, so that a report submitted
/// through the public form, a row in the database and a line in the triage queue always agree.
/// </remarks>
public static class AbuseReasonNames
{
    /// <summary>A target that impersonates someone in order to collect credentials.</summary>
    public const string Phishing = "phishing";

    /// <summary>A target that distributes malicious software.</summary>
    public const string Malware = "malware";

    /// <summary>Unsolicited bulk distribution.</summary>
    public const string Spam = "spam";

    /// <summary>Content that is unlawful in the operator's jurisdiction.</summary>
    public const string Illegal = "illegal";

    /// <summary>A claimed infringement of copyright.</summary>
    public const string Copyright = "copyright";

    /// <summary>Anything the categories above do not cover.</summary>
    public const string Other = "other";

    /// <summary>A report that has been received and not yet looked at.</summary>
    public const string StatusNew = "new";

    /// <summary>A report an operator has classified but not closed.</summary>
    public const string StatusTriaged = "triaged";

    /// <summary>A report the operator agreed with.</summary>
    public const string StatusConfirmed = "confirmed";

    /// <summary>A report the operator disagreed with.</summary>
    public const string StatusRejected = "rejected";

    /// <summary>A report that has been acted upon and closed.</summary>
    public const string StatusResolved = "resolved";

    /// <summary>Every accepted reason, in the order they are offered in the form.</summary>
    public static IReadOnlyList<string> Reasons { get; } =
        [Phishing, Malware, Spam, Illegal, Copyright, Other];

    /// <summary>Every status a report can be moved to by an operator.</summary>
    public static IReadOnlyList<string> ClosingStatuses { get; } =
        [StatusTriaged, StatusConfirmed, StatusRejected, StatusResolved];

    /// <summary>Normalises a submitted reason.</summary>
    /// <param name="value">The submitted value. Case and surrounding whitespace are ignored.</param>
    /// <param name="reason">The canonical spelling when the value is known.</param>
    /// <returns><see langword="true"/> when the value names a reason.</returns>
    /// <remarks>
    /// An unknown reason is refused rather than mapped to <see cref="Other"/>: silently reshaping a
    /// notice changes what the operator is answering, and under the notice and action duty of the
    /// Digital Services Act that answer is what has to be traceable.
    /// </remarks>
    public static bool TryNormalizeReason(string? value, out string reason)
    {
        reason = Other;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string candidate = value.Trim().ToLowerInvariant();

        foreach (string known in Reasons)
        {
            if (string.Equals(candidate, known, StringComparison.Ordinal))
            {
                reason = known;
                return true;
            }
        }

        return false;
    }

    /// <summary>Normalises a status an operator is moving a report to.</summary>
    /// <param name="value">The submitted value. Case and surrounding whitespace are ignored.</param>
    /// <param name="status">The canonical spelling when the value is known.</param>
    /// <returns><see langword="true"/> when the value names a status an operator may set.</returns>
    public static bool TryNormalizeClosingStatus(string? value, out string status)
    {
        status = StatusTriaged;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string candidate = value.Trim().ToLowerInvariant();

        foreach (string known in ClosingStatuses)
        {
            if (string.Equals(candidate, known, StringComparison.Ordinal))
            {
                status = known;
                return true;
            }
        }

        return false;
    }

    /// <summary>Maps a canonical reason back to the domain enumeration.</summary>
    /// <param name="reason">A canonical reason spelling.</param>
    /// <returns>The matching enumeration member, or <see cref="AbuseReason.Other"/>.</returns>
    public static AbuseReason ToReason(string? reason) => reason switch
    {
        Phishing => AbuseReason.Phishing,
        Malware => AbuseReason.Malware,
        Spam => AbuseReason.Spam,
        Illegal => AbuseReason.Illegal,
        Copyright => AbuseReason.Copyright,
        _ => AbuseReason.Other,
    };

    /// <summary>Maps a canonical status back to the domain enumeration.</summary>
    /// <param name="status">A canonical status spelling.</param>
    /// <returns>The matching enumeration member, or <see cref="AbuseReportStatus.New"/>.</returns>
    public static AbuseReportStatus ToStatus(string? status) => status switch
    {
        StatusTriaged => AbuseReportStatus.Triaged,
        StatusConfirmed => AbuseReportStatus.Confirmed,
        StatusRejected => AbuseReportStatus.Rejected,
        StatusResolved => AbuseReportStatus.Resolved,
        _ => AbuseReportStatus.New,
    };
}
