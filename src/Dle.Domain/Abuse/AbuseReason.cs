namespace Dle.Domain.Abuse;

/// <summary>
/// Category chosen by the reporter on the public abuse form (FR-245).
/// </summary>
public enum AbuseReason
{
    /// <summary>The target imitates someone else in order to harvest credentials or payments.</summary>
    Phishing = 0,

    /// <summary>The target distributes malicious software.</summary>
    Malware = 1,

    /// <summary>The link is used for unsolicited bulk messaging.</summary>
    Spam = 2,

    /// <summary>The target hosts content that is illegal in the operator's jurisdiction.</summary>
    Illegal = 3,

    /// <summary>The target infringes copyright.</summary>
    Copyright = 4,

    /// <summary>Anything the categories above do not cover; the details field carries the
    /// explanation.</summary>
    Other = 5,
}
