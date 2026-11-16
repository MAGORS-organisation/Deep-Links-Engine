using Dle.Control.Configuration;
using Dle.Control.Features.Shared;
using Dle.Control.Identity;
using Dle.Control.Infrastructure;

using Microsoft.Extensions.Options;

namespace Dle.Control.Features.Domains;

/// <summary>
/// <c>POST /api/v1/domains/{id}/verify</c> and <c>GET /api/v1/domains/{id}/verifications</c>.
/// </summary>
public static class VerifyDomain
{
    /// <summary>How many historical runs one request may read.</summary>
    private const int HistoryLimit = 50;

    /// <summary>Verifies DNS TXT ownership (<c>_dle-verify.&lt;host&gt;</c>) and activates the domain.</summary>
    /// <param name="id">The domain identifier.</param>
    /// <param name="http">The request, for the authenticated caller.</param>
    /// <param name="domains">Domain storage.</param>
    /// <param name="ownership">DNS TXT ownership verifier.</param>
    /// <param name="audit">Administrative audit trail.</param>
    /// <param name="cache">Drops any cached domain config the edge holds.</param>
    /// <param name="clock">Time provider.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Verification result or 404.</returns>
    public static async Task<IResult> VerifyOwnershipAsync(
        Guid id,
        HttpContext http,
        DomainRepository domains,
        DomainOwnershipVerifier ownership,
        AuditLogWriter audit,
        ILinkCacheInvalidator cache,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(domains);
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(clock);

        DleCaller caller = http.RequireDleCaller();
        LinkDomain? domain = await domains.GetAsync(id, cancellationToken);
        if (domain is null)
        {
            return DleProblem.NotFound("There is no domain with that identifier.");
        }

        DateTimeOffset checkedAt = clock.GetUtcNow();
        bool matched = await ownership.VerifyAsync(domain, cancellationToken);
        string txtHost = DomainOwnershipVerifier.GetTxtHost(domain.Host);

        if (matched)
        {
            domain.OwnershipVerifiedAt ??= checkedAt;
            domain.IsActive = true;
            await domains.UpdateAsync(domain, cancellationToken);

            await AdministrativeAudit.RecordAsync(
                audit,
                caller,
                AuditActions.DomainOwnershipVerified,
                AuditActions.DomainSubject,
                domain.Id.ToString(),
                new Dictionary<string, string>(StringComparer.Ordinal) { ["host"] = domain.Host },
                cancellationToken);

            await cache.InvalidateHostAsync(domain.Host, cancellationToken);
        }

        VerificationCheckResult check = matched
            ? new VerificationCheckResult
            {
                Kind = "ownership",
                Status = DomainVerificationService.OkStatus,
                Detail = $"DNS TXT record verified at {txtHost}.",
            }
            : new VerificationCheckResult
            {
                Kind = "ownership",
                Status = DomainVerificationService.FailedStatus,
                Codes = ["dns.txt_mismatch"],
                Detail = $"Expected TXT record at {txtHost} with value '{ownership.GetExpectedTxtValue(domain.TenantId, domain.Host)}'.",
            };

        return TypedResults.Ok(new DomainVerificationResponse
        {
            DomainId = domain.Id,
            Host = domain.Host,
            Ok = matched,
            CheckedAt = checkedAt,
            Checks = [check],
        });
    }

    /// <summary>Runs every association check against a domain and records the outcome.</summary>
    /// <param name="id">The domain identifier.</param>
    /// <param name="http">The request, for the authenticated caller.</param>
    /// <param name="domains">Domain storage.</param>
    /// <param name="verifier">The verification service.</param>
    /// <param name="ownership">DNS TXT ownership verifier.</param>
    /// <param name="controlOptions">Control-plane options.</param>
    /// <param name="audit">The administrative audit trail.</param>
    /// <param name="cache">Drops the cached association files the edge serves for the host.</param>
    /// <param name="clock">Time provider.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the result, or 404 when there is no such domain.</returns>
    /// <remarks>
    /// The response carries the seven-day propagation notice whether the run passed or failed. An
    /// operator who fixes an association file, sees a green tick, tests on a device that still holds
    /// the cached copy and concludes the fix did not work is the failure this notice exists to
    /// prevent.
    /// </remarks>
    public static async Task<IResult> HandleAsync(
        Guid id,
        HttpContext http,
        DomainRepository domains,
        DomainVerificationService verifier,
        DomainOwnershipVerifier ownership,
        IOptions<DleControlOptions> controlOptions,
        AuditLogWriter audit,
        ILinkCacheInvalidator cache,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(domains);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentNullException.ThrowIfNull(controlOptions);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(clock);

        DleCaller caller = http.RequireDleCaller();

        LinkDomain? domain = await domains.GetAsync(id, cancellationToken);

        if (domain is null)
        {
            return DleProblem.NotFound("There is no domain with that identifier.");
        }

        if (controlOptions.Value.RequireDomainOwnershipVerification
            && domain.OwnershipVerifiedAt is null
            && await ownership.VerifyAsync(domain, cancellationToken))
        {
            domain.OwnershipVerifiedAt = clock.GetUtcNow();
            domain.IsActive = true;
            await domains.UpdateAsync(domain, cancellationToken);
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
        // (they must be invalidated within fifteen minutes, and immediately is within fifteen minutes).
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
    /// intermittent and the useful question is "since when", not "right now".
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

/// <summary>One recorded verification run.</summary>
public sealed record DomainVerificationRecord
{
    /// <summary>What was checked: <c>dns</c>, <c>tls</c>, <c>aasa</c> or <c>assetlinks</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Outcome: <c>ok</c>, <c>warning</c> or <c>failed</c>.</summary>
    public required string Status { get; init; }

    /// <summary>HTTP status the association file answered with, when a request was made.</summary>
    public int? HttpStatus { get; init; }

    /// <summary>How many redirects were seen. Any value above zero fails the check.</summary>
    public required int RedirectCount { get; init; }

    /// <summary>When the run happened.</summary>
    public required DateTimeOffset CheckedAt { get; init; }
}
