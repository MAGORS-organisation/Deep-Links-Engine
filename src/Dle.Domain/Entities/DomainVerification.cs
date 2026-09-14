namespace Dle.Domain.Entities;

/// <summary>
/// One recorded run of a domain verification check. Maps to <c>domain_verifications</c> (FR-143).
/// </summary>
/// <remarks>
/// Kept as history rather than a single mutable status column, because association file problems
/// are usually intermittent and the useful question is "since when", not "right now". The nightly
/// verifier appends a row per domain per check kind, which is also what feeds the
/// <c>dle_domain_verification_failures</c> alert (§C.6, §C.8).
/// </remarks>
public class DomainVerification
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>Domain that was checked.</summary>
    public Guid DomainId { get; set; }

    /// <summary>What was checked: <c>dns</c>, <c>tls</c>, <c>aasa</c> or <c>assetlinks</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Outcome: <c>ok</c>, <c>warning</c> or <c>failed</c>.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>HTTP status the association file responded with, when the check made a request.</summary>
    public int? HttpStatus { get; set; }

    /// <summary>
    /// Number of redirects followed. Any value above zero fails the check: both Apple and Google
    /// refuse a redirected association file, and this is one of the most common silent breakages
    /// (§A.2.1, §A.2.2, TC-124).
    /// </summary>
    public int RedirectCount { get; set; }

    /// <summary>Issue codes and detail, stored as a JSON array.</summary>
    public string Issues { get; set; } = "[]";

    /// <summary>When the check ran.</summary>
    public DateTimeOffset CheckedAt { get; set; }
}
