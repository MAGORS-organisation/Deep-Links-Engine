using System.ComponentModel.DataAnnotations;

namespace Dle.Control.Features.Abuse;

/// <summary>
/// Configuration of the abuse module, bound from <c>Dle:Abuse</c> (SHARED-KERNEL §16, §E.3).
/// </summary>
/// <remarks>
/// The defaults are the ones a self-hoster can run without signing anything: URLhaus is free and
/// unrestricted, which is why it is the default reputation source. Google Safe Browsing v5 is
/// limited to non-commercial use and Web Risk is paid, so neither can be a default in an
/// open-source product — they belong behind the same provider seam, added by the deployment that
/// has the licence.
/// </remarks>
public sealed class AbuseOptions
{
    /// <summary>Name of the configuration section this type is bound from.</summary>
    public const string SectionName = "Dle:Abuse";

    /// <summary>Whether the URLhaus reputation source is consulted (§E.3 step 3).</summary>
    public bool UrlHausEnabled { get; set; } = true;

    /// <summary>Endpoint of the URLhaus lookup API.</summary>
    [Required(AllowEmptyStrings = false)]
    public string UrlHausEndpoint { get; set; } = "https://urlhaus-api.abuse.ch/v1/url/";

    /// <summary>
    /// Auth key sent as the <c>Auth-Key</c> header. abuse.ch has required one for API access since
    /// 2025; without it the provider is skipped rather than silently treated as a clean verdict.
    /// </summary>
    public string? UrlHausAuthKey { get; set; }

    /// <summary>Timeout for one reputation lookup, in seconds.</summary>
    [Range(1, 60)]
    public int LookupTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Path to a newline separated local blocklist of hosts, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// An operator's own list is authoritative over any remote source: it is how a deployment
    /// answers a complaint immediately rather than waiting for a feed to catch up. Lines beginning
    /// with <c>#</c> are comments.
    /// </remarks>
    public string? BlocklistPath { get; set; }

    /// <summary>How often the local blocklist file is re-read, in minutes.</summary>
    [Range(1, 1440)]
    public int BlocklistReloadMinutes { get; set; } = 5;

    /// <summary>How often active links are re-checked against the reputation sources, in hours.</summary>
    /// <remarks>
    /// Changing the target after creation is the classic abuse pattern, so a check that only ran at
    /// creation would be the one check an abuser plans around (§E.3 step 5).
    /// </remarks>
    [Range(1, 720)]
    public int RecheckIntervalHours { get; set; } = 24;

    /// <summary>How many reports one IP address may submit per hour (§E.9).</summary>
    [Range(1, 1000)]
    public int ReportsPerHourPerIp { get; set; } = 5;

    /// <summary>Longest free text a reporter may submit, in characters.</summary>
    [Range(0, 20_000)]
    public int MaxDetailsLength { get; set; } = 4_000;

    /// <summary>Largest triage page an operator may ask for.</summary>
    [Range(1, 1000)]
    public int MaxTriagePageSize { get; set; } = 200;

    /// <summary>
    /// Whether a re-check that finds a target malicious quarantines the link on its own.
    /// </summary>
    /// <remarks>
    /// On by default. The alternative — raise a report and wait for a human — sounds safer and is
    /// not: the window between a target turning malicious and an operator reading a queue is
    /// exactly the window the abuse is designed to fit into. Quarantine is reversible and leaves a
    /// full trail, so the cost of being wrong is a release, not a lost link.
    /// </remarks>
    public bool AutoQuarantineOnMaliciousRecheck { get; set; } = true;
}
