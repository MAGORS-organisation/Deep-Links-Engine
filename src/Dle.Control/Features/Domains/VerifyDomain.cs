using Dle.Control.Features.Shared;
using Dle.Control.Identity;
using Dle.Control.Infrastructure;

namespace Dle.Control.Features.Domains;

/// <summary>
/// <c>POST /api/v1/domains/{id}/verify</c> and <c>GET /api/v1/domains/{id}/verifications</c>
/// (FR-143, FR-144, TC-125).
/// </summary>
public static class VerifyDomain
{
    /// <summary>How many historical runs one request may read.</summary>
    private const int HistoryLimit = 50;

    /// <summary>Runs every association check against a domain and records the outcome.</summary>
    /// <param name="id">The domain identifier.</param>
    /// <param name="http">The request, for the authenticated caller.</param>
    /// <param name="domains">Domain storage.</param>
    /// <param name="verifier">The verification service.</param>
    /// <param name="audit">The administrative audit trail (FR-246).</param>
    /// <param name="cache">Drops the cached association files the edge serves for the host.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the result, or 404 when there is no such domain.</returns>
    /// <remarks>
    /// The response carries the seven-day propagation notice whether the run passed or failed. An
    /// operator who fixes an association file, sees a green tick, tests on a device that still holds
    /// the cached copy and concludes the fix did not work is the failure this notice exists to
    /// prevent (TC-125).
    /// </remarks>
    public static async Task<IResult> HandleAsync(
        Guid id,
        HttpContext http,
        DomainRepository domains,
        DomainVerificationService verifier,
        AuditLogWriter audit,
        ILinkCacheInvalidator cache,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(domains);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(cache);

        DleCaller caller = http.RequireDleCaller();

        LinkDomain? domain = await domains.GetAsync(id, cancellationToken);

        if (domain is null)
        {
            return DleProblem.NotFound("There is no domain with that identifier.");
        }

        DomainVerificationRun run = await verifier.VerifyAsync(domain, cancellationToken);

        foreach (DomainVerification record in run.Runs)
        {
            await domains.RecordVerificationAsync(record, cancellationToken);
        }

        await AdministrativeAudit.RecordAsync(
            audit,
            caller,
            AuditActions.DomainVerified,
            AuditActions.DomainSubject,
            domain.Id.ToString(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["host"] = domain.Host,
                ["ok"] = run.Response.Ok ? "true" : "false",
            },
            cancellationToken);

        // The edge caches the generated association files per host. A verification run is the moment
        // the operator expects the engine's own files to be regenerated, so the cached copies go
        // (TC-125: invalidated within fifteen minutes, and immediately is within fifteen minutes).
        await cache.InvalidateHostAsync(domain.Host, cancellationToken);

        return TypedResults.Ok(run.Response);
    }

    /// <summary>Reads the recent verification runs of a domain.</summary>
    /// <param name="id">The domain identifier.</param>
    /// <param name="domains">Domain storage.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The runs, newest first, or 404.</returns>
    /// <remarks>
    /// History rather than a single status column, because association file problems are usually
    /// intermittent and the useful question is "since when", not "right now" (§C.6, §C.8).
    /// </remarks>
    public static async Task<IResult> HistoryAsync(
        Guid id,
        DomainRepository domains,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domains);

        LinkDomain? domain = await domains.GetAsync(id, cancellationToken);

        if (domain is null)
        {
            return DleProblem.NotFound("There is no domain with that identifier.");
        }

        IReadOnlyList<DomainVerification> runs =
            await domains.GetVerificationHistoryAsync(id, HistoryLimit, cancellationToken);

        List<DomainVerificationRecord> items = new(runs.Count);

        foreach (DomainVerification run in runs)
        {
            items.Add(new DomainVerificationRecord
            {
                Kind = run.Kind,
                Status = run.Status,
                HttpStatus = run.HttpStatus,
                RedirectCount = run.RedirectCount,
                CheckedAt = run.CheckedAt,
            });
        }

        return TypedResults.Ok(new PagedResponse<DomainVerificationRecord>
        {
            Items = items,
            Total = items.Count,
        });
    }
}

/// <summary>One recorded verification run (FR-143).</summary>
public sealed record DomainVerificationRecord
{
    /// <summary>What was checked: <c>dns</c>, <c>tls</c>, <c>aasa</c> or <c>assetlinks</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Outcome: <c>ok</c>, <c>warning</c> or <c>failed</c>.</summary>
    public required string Status { get; init; }

    /// <summary>HTTP status the association file answered with, when a request was made.</summary>
    public int? HttpStatus { get; init; }

    /// <summary>How many redirects were seen. Any value above zero fails the check (TC-124).</summary>
    public required int RedirectCount { get; init; }

    /// <summary>When the run happened.</summary>
    public required DateTimeOffset CheckedAt { get; init; }
}
