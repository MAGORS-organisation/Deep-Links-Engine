namespace Dle.Control.Configuration;

/// <summary>
/// Names of the rate limiter policies. Every control-plane route carries exactly one (§E.9).
/// </summary>
/// <remarks>
/// Constants rather than literals at the call site, because the middleware resolves a policy by
/// name at request time: a typo is not a compile error, it is a route that silently runs
/// unlimited. Naming them here makes the set enumerable and makes a missing registration fail at
/// startup instead of in production.
/// </remarks>
public static class DleRateLimitPolicies
{
    /// <summary>
    /// Link creation: sixty a minute per API key, ten a minute while the tenant is new (§E.9).
    /// </summary>
    public const string LinkCreate = "dle-link-create";

    /// <summary>Streamed bulk import: two concurrent batches per tenant (§E.9).</summary>
    public const string Bulk = "dle-bulk";

    /// <summary>Ordinary reads.</summary>
    public const string Read = "dle-read";

    /// <summary>Administrative writes other than link creation.</summary>
    public const string Write = "dle-write";
}
