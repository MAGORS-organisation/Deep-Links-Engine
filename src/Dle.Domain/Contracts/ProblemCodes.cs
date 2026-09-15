using System.Collections.Immutable;

namespace Dle.Domain.Contracts;

/// <summary>
/// Stable identifiers used as the <c>type</c> member of an RFC 9457 problem document.
/// </summary>
/// <remarks>
/// These strings are part of the public API surface. Integrators branch on them, so they are
/// versioned like any other contract: a code may be added, but an existing one never changes
/// meaning and never disappears without a major version. The human readable explanation lives at
/// the URI; the URI itself is an identifier and is not required to resolve.
/// </remarks>
public static class ProblemCodes
{
    /// <summary>Base URI that every problem type is derived from.</summary>
    public const string Base = "https://docs.dle.dev/problems/";

    /// <summary>Request body or query failed validation. Carries an <c>errors</c> extension.</summary>
    public const string ValidationFailed = Base + "validation-failed";

    /// <summary>The requested slug is already taken on the target domain.</summary>
    public const string SlugTaken = Base + "slug-taken";

    /// <summary>The slug is syntactically invalid or reserved.</summary>
    public const string SlugInvalid = Base + "slug-invalid";

    /// <summary>The target URL failed the safety policy (§E.3, T-01, T-02).</summary>
    public const string UnsafeTarget = Base + "unsafe-target";

    /// <summary>A rate limit or quota was exceeded. Carries <c>Retry-After</c> (§E.9).</summary>
    public const string RateLimited = Base + "rate-limited";

    /// <summary>The routing rule set has no default rule, so it cannot be stored (TC-105).</summary>
    public const string MissingDefaultRule = Base + "missing-default-rule";

    /// <summary>The routing rule set is otherwise invalid. Carries an <c>errors</c> extension.</summary>
    public const string InvalidRoutingRules = Base + "invalid-routing-rules";

    /// <summary>The domain exists but has not passed AASA/assetlinks verification (FR-143).</summary>
    public const string DomainNotVerified = Base + "domain-not-verified";

    /// <summary>The host is already registered, possibly by another tenant.</summary>
    public const string DomainTaken = Base + "domain-taken";

    /// <summary>An <c>Idempotency-Key</c> was replayed with a different request body.</summary>
    public const string IdempotencyConflict = Base + "idempotency-conflict";

    /// <summary>The supplied credential is missing, malformed or expired.</summary>
    public const string Unauthorized = Base + "unauthorized";

    /// <summary>The credential is valid but lacks the scope this operation needs.</summary>
    public const string Forbidden = Base + "forbidden";

    /// <summary>The claim code is unknown, already consumed or past its TTL (TC-148).</summary>
    public const string ClaimCodeInvalid = Base + "claim-code-invalid";

    /// <summary>The link is quarantined following an abuse report (TC-103).</summary>
    public const string LinkQuarantined = Base + "link-quarantined";

    /// <summary>A dependency the request needs is unavailable; the caller may retry.</summary>
    public const string DependencyUnavailable = Base + "dependency-unavailable";

    /// <summary>
    /// The resource changed between the caller's read and its write (a concurrent edit, or an
    /// abuse quarantine), so the write was refused rather than allowed to reverse the newer
    /// change. Read again and retry with the current state.
    /// </summary>
    public const string WriteConflict = Base + "write-conflict";

    /// <summary>
    /// The link's resolve-time fields are together larger than the covering index can hold
    /// (§B.5.2); shorten the target URL, the expired URL, the title, the UTM set or the rules.
    /// </summary>
    public const string LinkTooLarge = Base + "link-too-large";

    /// <summary>
    /// An application with this platform and bundle identifier is already registered in the
    /// tenant (FR-140). Update the existing registration instead of adding a second one.
    /// </summary>
    public const string AppTaken = Base + "app-taken";

    /// <summary>
    /// Every identifier declared above, in declaration order.
    /// </summary>
    /// <remarks>
    /// The set exists so that the published OpenAPI document can enumerate the codes an integrator
    /// may have to branch on (§B.7.3). Without it the identifiers would be discoverable only by
    /// reading this file or by provoking each failure against a running server. A new code is added
    /// here in the same change that declares it; nothing else in the product is allowed to keep a
    /// second copy of the list.
    /// </remarks>
    public static readonly ImmutableArray<string> All =
    [
        ValidationFailed,
        SlugTaken,
        SlugInvalid,
        UnsafeTarget,
        RateLimited,
        MissingDefaultRule,
        InvalidRoutingRules,
        DomainNotVerified,
        DomainTaken,
        IdempotencyConflict,
        Unauthorized,
        Forbidden,
        ClaimCodeInvalid,
        LinkQuarantined,
        DependencyUnavailable,
        WriteConflict,
        LinkTooLarge,
        AppTaken,
    ];
}
