using Dle.Persistence.Internal;

namespace Dle.Persistence.Repositories;

/// <summary>
/// Reads and writes abuse reports and the quarantine state of links (FR-245, §E.3).
/// </summary>
/// <remarks>
/// The abuse form is public, and triage is the instance operator's job rather than the reported
/// tenant's, so most of this class works across tenants — deliberately, in named scopes. A link
/// shortener carrying user supplied content is very likely a hosting service under the Digital
/// Services Act, whose article 16 requires a notice and action mechanism with a traceable outcome:
/// that is what the status transitions and the resolution note are for.
/// </remarks>
public sealed class AbuseRepository
{
    private readonly DleDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly LinkRepository _links;

    /// <summary>Creates the repository.</summary>
    /// <param name="db">The control-plane context.</param>
    /// <param name="timeProvider">Clock used to stamp reports and quarantine decisions.</param>
    public AbuseRepository(DleDbContext db, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _db = db;
        _timeProvider = timeProvider;

        // Quarantine and release are versioned edits of the link, written the same way and with
        // the same guarantees as an operator's edit; the link repository owns that write path.
        _links = new LinkRepository(db, timeProvider);
    }

    /// <summary>
    /// Records a report submitted through the public form.
    /// </summary>
    /// <param name="linkId">The reported link.</param>
    /// <param name="reason">Category: <c>phishing</c>, <c>malware</c>, <c>spam</c>, <c>illegal</c>,
    /// <c>copyright</c> or <c>other</c>.</param>
    /// <param name="details">Free text supplied by the reporter.</param>
    /// <param name="reporterEmailHash">Hash of the reporter's address, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored report, or <see langword="null"/> when the link does not exist.</returns>
    /// <remarks>
    /// The reporter is not authenticated and has no tenant, so the tenant is taken from the link
    /// being reported. The email address is hashed, never stored: the operator needs to recognise a
    /// repeat reporter and deduplicate, which a hash answers, and does not need a list of people
    /// who reported someone, which is what an address column would be.
    /// </remarks>
    public async Task<AbuseReport?> CreateReportAsync(
        long linkId,
        string reason,
        string? details,
        byte[]? reporterEmailHash,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        using CrossTenantScope scope = _db.BeginCrossTenantScope(
            "the public abuse form has no authenticated tenant; the reported link supplies it");

        Guid? tenantId = await _db.Links
            .AsNoTracking()
            .AcrossTenants()
            .IncludeSoftDeleted()
            .Where(l => l.Id == linkId)
            .Select(l => (Guid?)l.TenantId)
            .FirstOrDefaultAsync(cancellationToken);

        if (tenantId is not Guid owner)
        {
            return null;
        }

        AbuseReport report = new()
        {
            LinkId = linkId,
            TenantId = owner,
            Reason = reason,
            Details = details,
            ReporterEmailHash = reporterEmailHash,
            Status = "new",
            CreatedAt = _timeProvider.GetUtcNow(),
        };

        _db.AbuseReports.Add(report);
        await _db.SaveChangesAsync(cancellationToken);
        return report;
    }

    /// <summary>Lists the reports of the tenant in scope.</summary>
    /// <param name="status">Restrict to one status, or <see langword="null"/> for all.</param>
    /// <param name="limit">Maximum number of rows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reports, oldest first — the reaction target runs from submission.</returns>
    public async Task<IReadOnlyList<AbuseReport>> ListAsync(
        string? status,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        IQueryable<AbuseReport> query = _db.AbuseReports.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(r => r.Status == status);
        }

        return await query
            .OrderBy(r => r.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Lists reports of every tenant, for the instance operator's triage queue.</summary>
    /// <param name="status">Restrict to one status, or <see langword="null"/> for all.</param>
    /// <param name="limit">Maximum number of rows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reports, oldest first.</returns>
    public async Task<IReadOnlyList<AbuseReport>> ListForTriageAsync(
        string? status,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        using CrossTenantScope scope = _db.BeginCrossTenantScope(
            "abuse triage is the instance operator's duty and spans every tenant");

        IQueryable<AbuseReport> query = _db.AbuseReports.AsNoTracking().AcrossTenants();

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(r => r.Status == status);
        }

        return await query
            .OrderBy(r => r.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Closes a report with an outcome.</summary>
    /// <param name="reportId">The report.</param>
    /// <param name="status">The closing status: <c>triaged</c>, <c>confirmed</c>, <c>rejected</c>
    /// or <c>resolved</c>.</param>
    /// <param name="resolutionNote">What was decided and why.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when a row was updated.</returns>
    public async Task<bool> ResolveReportAsync(
        Guid reportId,
        string status,
        string? resolutionNote,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        using CrossTenantScope scope = _db.BeginCrossTenantScope(
            "abuse triage is the instance operator's duty and spans every tenant");

        AbuseReport? report = await _db.AbuseReports
            .AcrossTenants()
            .FirstOrDefaultAsync(r => r.Id == reportId, cancellationToken);

        if (report is null)
        {
            return false;
        }

        report.Status = status;
        report.ResolutionNote = resolutionNote;
        report.ResolvedAt = _timeProvider.GetUtcNow();

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Withdraws a link from service without deleting it (TC-103).
    /// </summary>
    /// <param name="linkId">The link.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the link was quarantined by this call.</returns>
    /// <remarks>
    /// Quarantine, not deletion. A withdrawn link answers 410 Gone with an explanation, which a
    /// deleted one could not: quiet removal turns a moderated link into an unknown one and destroys
    /// the trail a later complaint has to be answered from. The soft delete filter then keeps it out
    /// of the tenant's ordinary lists while the row itself stays exactly where the edge can find it.
    /// </remarks>
    public async Task<bool> QuarantineLinkAsync(long linkId, CancellationToken cancellationToken)
    {
        using CrossTenantScope scope = _db.BeginCrossTenantScope(
            "abuse enforcement acts on a link regardless of which tenant owns it");

        return await ChangeLinkStateAsync(
            linkId,
            static link => link.QuarantinedAt is null,
            link => link.QuarantinedAt = _timeProvider.GetUtcNow(),
            "Quarantined by abuse enforcement.",
            cancellationToken);
    }

    /// <summary>
    /// Applies a state change to a link as a versioned edit, so that the change shows up in the
    /// link's history and so that an edit prepared before it cannot silently reverse it.
    /// </summary>
    /// <remarks>
    /// The version is the link's concurrency token. Enforcement must win against a concurrent
    /// edit rather than lose to it, so a version conflict here is retried on a fresh read; three
    /// attempts is far beyond what two racing writes need.
    /// </remarks>
    private async Task<bool> ChangeLinkStateAsync(
        long linkId,
        Func<Link, bool> applies,
        Action<Link> change,
        string changeNote,
        CancellationToken cancellationToken)
    {
        const int attempts = 3;

        for (int attempt = 1; ; attempt++)
        {
            Link? link = await _db.Links
                .AcrossTenants()
                .IncludeSoftDeleted()
                .FirstOrDefaultAsync(l => l.Id == linkId, cancellationToken);

            if (link is null || !applies(link))
            {
                return false;
            }

            int readVersion = link.Version;
            change(link);
            link.Version = readVersion + 1;
            LinkVersion revision = LinkRevisions.Create(link, changedBy: null, changeNote, _timeProvider.GetUtcNow());

            try
            {
                await _links.WriteVersionedAsync(link, readVersion, revision, cancellationToken);
                return true;
            }
            catch (DbUpdateConcurrencyException) when (attempt < attempts)
            {
                // Somebody else wrote the row between the read and the write. Forget what this
                // attempt staged and decide again from what is now in the database.
                _db.Entry(revision).State = EntityState.Detached;
                _db.Entry(link).State = EntityState.Detached;
            }
        }
    }

    /// <summary>Returns a quarantined link to service after a successful appeal.</summary>
    /// <param name="linkId">The link.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the link was released by this call.</returns>
    public async Task<bool> ReleaseLinkAsync(long linkId, CancellationToken cancellationToken)
    {
        using CrossTenantScope scope = _db.BeginCrossTenantScope(
            "abuse enforcement acts on a link regardless of which tenant owns it");

        // Dropping the soft delete filter by name is the only way to reach a quarantined row; the
        // tenant filter is dropped separately and for its own stated reason.
        return await ChangeLinkStateAsync(
            linkId,
            static link => link.QuarantinedAt is not null,
            static link => link.QuarantinedAt = null,
            "Released from quarantine after review.",
            cancellationToken);
    }
}
