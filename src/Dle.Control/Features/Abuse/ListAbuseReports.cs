using System.Globalization;

using Dle.Control.Features.Shared;
using Dle.Domain.Contracts;
using Dle.Domain.Entities;
using Dle.Persistence.Repositories;
using Dle.Persistence.Tenancy;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Dle.Control.Features.Abuse;

/// <summary>
/// Reading side of the abuse queue: the tenant's own reports, and the instance operator's triage
/// queue across every tenant (§E.3 step 9).
/// </summary>
/// <remarks>
/// Two lists rather than one, because they answer to different people. A tenant may see reports
/// filed against its own links, and only those. The instance operator triages every tenant's
/// reports, because deciding whether a notice is well founded is the operator's duty under the
/// notice and action mechanism, not the duty of the party being complained about.
/// </remarks>
public static class ListAbuseReports
{
    /// <summary>Default page size when the caller does not ask for one.</summary>
    private const int DefaultLimit = 50;

    /// <summary>Lists the reports filed against the calling tenant's links.</summary>
    /// <param name="status">Restrict to one status, or <see langword="null"/> for all.</param>
    /// <param name="limit">Maximum number of rows.</param>
    /// <param name="reports">Report storage.</param>
    /// <param name="tenantContext">The tenant in scope.</param>
    /// <param name="options">Abuse options, read for the page size cap.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reports, oldest first.</returns>
    public static async Task<IResult> HandleTenantAsync(
        string? status,
        int? limit,
        AbuseRepository reports,
        ITenantContext tenantContext,
        IOptionsMonitor<AbuseOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentNullException.ThrowIfNull(options);

        if (tenantContext.TenantId is null)
        {
            return DleProblemResults.Unauthorized(
                "This endpoint is scoped to a tenant and the request established none.");
        }

        if (!TryReadPage(status, limit, options.CurrentValue, out int pageSize, out IResult failure))
        {
            return failure;
        }

        IReadOnlyList<AbuseReport> rows = await reports.ListAsync(status, pageSize, cancellationToken);

        return TypedResults.Ok(new PagedResponse<AbuseReportDetailResponse>
        {
            Items = Project(rows),
            Total = rows.Count,
        });
    }

    /// <summary>Lists the instance operator's triage queue across every tenant.</summary>
    /// <param name="status">Restrict to one status, or <see langword="null"/> for all.</param>
    /// <param name="limit">Maximum number of rows.</param>
    /// <param name="reports">Report storage.</param>
    /// <param name="options">Abuse options, read for the page size cap.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reports, oldest first — the reaction target runs from submission.</returns>
    public static async Task<IResult> HandleTriageAsync(
        string? status,
        int? limit,
        AbuseRepository reports,
        IOptionsMonitor<AbuseOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(options);

        if (!TryReadPage(status, limit, options.CurrentValue, out int pageSize, out IResult failure))
        {
            return failure;
        }

        IReadOnlyList<AbuseReport> rows =
            await reports.ListForTriageAsync(status, pageSize, cancellationToken);

        return TypedResults.Ok(new PagedResponse<AbuseReportDetailResponse>
        {
            Items = Project(rows),
            Total = rows.Count,
        });
    }

    /// <summary>Validates the paging and filtering parameters.</summary>
    /// <param name="status">The requested status filter.</param>
    /// <param name="limit">The requested page size.</param>
    /// <param name="options">Abuse options.</param>
    /// <param name="pageSize">The page size to apply.</param>
    /// <param name="failure">The problem document when validation failed.</param>
    /// <returns><see langword="true"/> when the parameters are usable.</returns>
    private static bool TryReadPage(
        string? status,
        int? limit,
        AbuseOptions options,
        out int pageSize,
        out IResult failure)
    {
        pageSize = Math.Clamp(limit ?? DefaultLimit, 1, options.MaxTriagePageSize);

        if (!string.IsNullOrWhiteSpace(status)
            && !AbuseReasonNames.TryNormalizeClosingStatus(status, out _)
            && !string.Equals(status, AbuseReasonNames.StatusNew, StringComparison.Ordinal))
        {
            failure = DleProblemResults.ValidationFailed(
                "The status filter is not a known report status.",
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["status"] =
                    [
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"Known statuses: {AbuseReasonNames.StatusNew}, "
                            + $"{string.Join(", ", AbuseReasonNames.ClosingStatuses)}."),
                    ],
                });

            return false;
        }

        failure = TypedResults.Empty;
        return true;
    }

    /// <summary>Renders stored reports for an operator.</summary>
    /// <param name="rows">The stored reports.</param>
    /// <returns>The rendered rows.</returns>
    private static List<AbuseReportDetailResponse> Project(IReadOnlyList<AbuseReport> rows)
    {
        List<AbuseReportDetailResponse> items = new(rows.Count);

        foreach (AbuseReport row in rows)
        {
            items.Add(new AbuseReportDetailResponse
            {
                Id = row.Id,
                TenantId = row.TenantId,
                LinkId = row.LinkId.ToString(CultureInfo.InvariantCulture),
                Reason = row.Reason,
                Details = row.Details,
                Status = row.Status,
                HasReporterContact = row.ReporterEmailHash is { Length: > 0 },
                CreatedAt = row.CreatedAt,
                ResolvedAt = row.ResolvedAt,
                ResolutionNote = row.ResolutionNote,
            });
        }

        return items;
    }
}

/// <summary>
/// An abuse report as an operator sees it.
/// </summary>
/// <remarks>
/// Wider than <see cref="AbuseReportResponse"/>, which is what the anonymous reporter receives and
/// deliberately carries nothing but an identifier, a status and a time. The reporter's address
/// never appears in either: only the fact that there is one, so the operator knows whether a reply
/// is possible at all.
/// </remarks>
public sealed record AbuseReportDetailResponse
{
    /// <summary>Report identifier.</summary>
    public required Guid Id { get; init; }

    /// <summary>Tenant that owns the reported link.</summary>
    public required Guid TenantId { get; init; }

    /// <summary>Reported link, as text because link identifiers are 64 bit.</summary>
    public required string LinkId { get; init; }

    /// <summary>Reported reason.</summary>
    public required string Reason { get; init; }

    /// <summary>Free text supplied by the reporter.</summary>
    public string? Details { get; init; }

    /// <summary>Current status.</summary>
    public required string Status { get; init; }

    /// <summary>Whether the reporter left a contact address, which is stored only as a hash.</summary>
    public required bool HasReporterContact { get; init; }

    /// <summary>When the report was received. The reaction target runs from here.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the report was closed, if it has been.</summary>
    public DateTimeOffset? ResolvedAt { get; init; }

    /// <summary>What the operator decided and why.</summary>
    public string? ResolutionNote { get; init; }
}
