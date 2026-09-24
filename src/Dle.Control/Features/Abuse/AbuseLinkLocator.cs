using Dle.Domain.Primitives;
using Dle.Persistence;
using Dle.Persistence.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace Dle.Control.Features.Abuse;

/// <summary>
/// Resolves a reported short URL to the link it names, across every tenant.
/// </summary>
/// <remarks>
/// <para>
/// The public form has no credential and therefore no tenant, so the lookup has to cross tenants —
/// in a named scope, with a written reason, which is how <see cref="DleDbContext"/> requires it to
/// be done.
/// </para>
/// <para>
/// A quarantined link is still found. A second report about a link that was already withdrawn is
/// not noise: it is evidence about how widely the link was distributed before it was stopped, and
/// under the notice and action duty it is a notice that still has to be answered.
/// </para>
/// </remarks>
public sealed class AbuseLinkLocator
{
    private readonly DleDbContext _db;

    /// <summary>Creates the locator.</summary>
    /// <param name="db">The control-plane context.</param>
    /// <exception cref="ArgumentNullException"><paramref name="db"/> is <see langword="null"/>.</exception>
    public AbuseLinkLocator(DleDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <summary>
    /// Finds the link a reported URL names.
    /// </summary>
    /// <param name="reportedUrl">The URL as the reporter pasted it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The link identifier and its tenant, or <see langword="null"/> when the URL is not one of
    /// this instance's short links.
    /// </returns>
    public async Task<LocatedLink?> FindAsync(string? reportedUrl, CancellationToken cancellationToken)
    {
        if (!TrySplit(reportedUrl, out string host, out string slug))
        {
            return null;
        }

        using CrossTenantScope scope = _db.BeginCrossTenantScope(
            "the public abuse form has no authenticated tenant; the reported URL names the link");

        Guid domainId = await _db.Domains
            .AsNoTracking()
            .AcrossTenants()
            .Where(d => d.Host == host)
            .Select(d => d.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (domainId == Guid.Empty)
        {
            return null;
        }

        LocatedLink? located = await _db.Links
            .AsNoTracking()
            .AcrossTenants()
            .IncludeSoftDeleted()
            .Where(l => l.DomainId == domainId && l.Slug == slug)
            .Select(l => new LocatedLink(l.Id, l.TenantId, l.QuarantinedAt != null))
            .FirstOrDefaultAsync(cancellationToken);

        return located;
    }

    /// <summary>
    /// Loads a link by identifier across tenants, for an operator acting on a report.
    /// </summary>
    /// <param name="linkId">The link identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The link, or <see langword="null"/>.</returns>
    public async Task<LocatedLink?> FindByIdAsync(long linkId, CancellationToken cancellationToken)
    {
        using CrossTenantScope scope = _db.BeginCrossTenantScope(
            "abuse enforcement acts on a link regardless of which tenant owns it");

        return await _db.Links
            .AsNoTracking()
            .AcrossTenants()
            .IncludeSoftDeleted()
            .Where(l => l.Id == linkId)
            .Select(l => new LocatedLink(l.Id, l.TenantId, l.QuarantinedAt != null))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Finds which tenant a report belongs to, so an audit entry about it lands in that tenant's
    /// trail rather than nowhere.
    /// </summary>
    /// <param name="reportId">The report identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tenant, or <see langword="null"/> when the report does not exist.</returns>
    public async Task<Guid?> FindReportTenantAsync(Guid reportId, CancellationToken cancellationToken)
    {
        using CrossTenantScope scope = _db.BeginCrossTenantScope(
            "abuse triage is the instance operator's duty and spans every tenant");

        Guid tenantId = await _db.AbuseReports
            .AsNoTracking()
            .AcrossTenants()
            .Where(r => r.Id == reportId)
            .Select(r => r.TenantId)
            .FirstOrDefaultAsync(cancellationToken);

        return tenantId == Guid.Empty ? null : tenantId;
    }

    /// <summary>
    /// Splits a reported URL into the normalised host and slug.
    /// </summary>
    /// <param name="reportedUrl">The URL as submitted. A bare <c>host/slug</c> is accepted too,
    /// because that is how people paste short links.</param>
    /// <param name="host">The normalised host.</param>
    /// <param name="slug">The normalised slug.</param>
    /// <returns><see langword="true"/> when both parts could be read.</returns>
    internal static bool TrySplit(string? reportedUrl, out string host, out string slug)
    {
        host = string.Empty;
        slug = string.Empty;

        if (string.IsNullOrWhiteSpace(reportedUrl))
        {
            return false;
        }

        string candidate = reportedUrl.Trim();

        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = "https://" + candidate;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (!HostNormalizer.TryNormalize(uri.Host, out host))
        {
            return false;
        }

        string path = uri.AbsolutePath.Trim('/');

        if (path.Length == 0)
        {
            return false;
        }

        int separator = path.IndexOf('/');
        string first = separator < 0 ? path : path[..separator];

        return SlugPolicy.TryNormalize(Uri.UnescapeDataString(first), out slug);
    }
}

/// <summary>A link found by the abuse pipeline.</summary>
/// <param name="LinkId">The link identifier.</param>
/// <param name="TenantId">The tenant that owns it.</param>
/// <param name="IsQuarantined">Whether it is already withdrawn from service.</param>
public sealed record LocatedLink(long LinkId, Guid TenantId, bool IsQuarantined);
