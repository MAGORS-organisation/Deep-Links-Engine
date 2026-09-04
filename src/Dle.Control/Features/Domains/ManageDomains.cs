using Dle.Control.Features.Shared;
using Dle.Control.Identity;
using Dle.Control.Infrastructure;

using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace Dle.Control.Features.Domains;

/// <summary>
/// <c>GET</c>, <c>POST</c>, <c>PATCH</c> and <c>DELETE /api/v1/domains</c> (FR-145).
/// </summary>
/// <remarks>
/// A domain cannot be renamed. Every short URL already in circulation names the host, so a rename
/// would break all of them at once, and the association files on the old host would keep claiming an
/// application that no longer serves it. Moving to a new host is register-and-retire, which is also
/// the only sequence in which both hosts stay correct throughout (§A.2.1).
/// </remarks>
public static class ManageDomains
{
    /// <summary>PostgreSQL SQLSTATE for a unique violation.</summary>
    private const string UniqueViolation = "23505";

    /// <summary>Lists the tenant's domains.</summary>
    /// <param name="domains">Domain storage.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The domains, ordered by host.</returns>
    public static async Task<IResult> ListAsync(
        DomainRepository domains,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domains);

        IReadOnlyList<LinkDomain> rows = await domains.ListAsync(cancellationToken);
        List<DomainResponse> items = new(rows.Count);

        foreach (LinkDomain row in rows)
        {
            items.Add(Project(row));
        }

        return TypedResults.Ok(new PagedResponse<DomainResponse>
        {
            Items = items,
            Total = items.Count,
        });
    }

    /// <summary>Reads one domain.</summary>
    /// <param name="id">The domain identifier.</param>
    /// <param name="domains">Domain storage.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the domain, or 404.</returns>
    public static async Task<IResult> GetAsync(
        Guid id,
        DomainRepository domains,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domains);

        LinkDomain? domain = await domains.GetAsync(id, cancellationToken);

        return domain is null
            ? DleProblem.NotFound("There is no domain with that identifier.")
            : TypedResults.Ok(Project(domain));
    }

    /// <summary>Registers a domain (FR-145).</summary>
    /// <param name="request">The host to register.</param>
    /// <param name="http">The request, for the authenticated caller.</param>
    /// <param name="domains">Domain storage.</param>
    /// <param name="tenants">Tenant storage, read for the consent mode the override may not widen.</param>
    /// <param name="audit">The administrative audit trail (FR-246).</param>
    /// <param name="cache">Drops any cached configuration the edge holds for the host.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>201 with the domain, or a problem document.</returns>
    public static async Task<IResult> CreateAsync(
        CreateDomainRequest request,
        HttpContext http,
        DomainRepository domains,
        TenantRepository tenants,
        AuditLogWriter audit,
        ILinkCacheInvalidator cache,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(domains);
        ArgumentNullException.ThrowIfNull(tenants);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(cache);

        DleCaller caller = http.RequireDleCaller();

        if (!HostNormalizer.TryNormalize(request.Host, out string host))
        {
            return DleProblem.Validation(
                "host",
                "The host is not a usable domain name. Register a host such as link.customer.example.");
        }

        Tenant? tenant = await tenants.GetAsync(caller.TenantId, cancellationToken);

        if (tenant is null)
        {
            return DleProblem.NotFound("The tenant no longer exists.");
        }

        if (!TryReadConsentOverride(
                request.ConsentModeOverride,
                tenant.ConsentMode,
                out string? consentOverride,
                out IResult? consentFailure))
        {
            return consentFailure!;
        }

        LinkDomain domain = new()
        {
            TenantId = caller.TenantId,
            Host = host,
            IsDefault = request.IsDefault,
            ConsentModeOverride = consentOverride,
            DefaultOg = ControlJson.WriteOgMeta(request.DefaultOg),
            IsActive = true,
        };

        try
        {
            await domains.AddAsync(domain, cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // The host is taken. The answer says so without saying by whom: which tenant holds a host
            // is not the caller's business, and a message that revealed it would be a disclosure
            // dressed up as a validation error.
            return DleProblem.Conflict(
                ProblemCodes.DomainTaken,
                "The host is already registered.",
                "That host is already registered on this instance. A host serves exactly one tenant.");
        }

        await AdministrativeAudit.RecordAsync(
            audit,
            caller,
            AuditActions.DomainCreated,
            AuditActions.DomainSubject,
            domain.Id.ToString(),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["host"] = host },
            cancellationToken);

        await cache.InvalidateHostAsync(host, cancellationToken);

        return TypedResults.Created(
            string.Create(CultureInfo.InvariantCulture, $"/api/v1/domains/{domain.Id}"),
            Project(domain));
    }

    /// <summary>Changes a domain.</summary>
    /// <param name="id">The domain identifier.</param>
    /// <param name="request">The fields to change.</param>
    /// <param name="http">The request, for the authenticated caller.</param>
    /// <param name="domains">Domain storage.</param>
    /// <param name="tenants">Tenant storage, read for the consent mode the override may not widen.</param>
    /// <param name="audit">The administrative audit trail (FR-246).</param>
    /// <param name="cache">Drops the cached configuration the edge holds for the host.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the domain, or a problem document.</returns>
    public static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateDomainRequest request,
        HttpContext http,
        DomainRepository domains,
        TenantRepository tenants,
        AuditLogWriter audit,
        ILinkCacheInvalidator cache,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(domains);
        ArgumentNullException.ThrowIfNull(tenants);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(cache);

        DleCaller caller = http.RequireDleCaller();

        LinkDomain? domain = await domains.GetAsync(id, cancellationToken);

        if (domain is null)
        {
            return DleProblem.NotFound("There is no domain with that identifier.");
        }

        if (request.ConsentModeOverride is not null)
        {
            Tenant? tenant = await tenants.GetAsync(caller.TenantId, cancellationToken);

            if (tenant is null)
            {
                return DleProblem.NotFound("The tenant no longer exists.");
            }

            if (!TryReadConsentOverride(
                    request.ConsentModeOverride,
                    tenant.ConsentMode,
                    out string? consentOverride,
                    out IResult? consentFailure))
            {
                return consentFailure!;
            }

            domain.ConsentModeOverride = consentOverride;
        }

        domain.IsDefault = request.IsDefault ?? domain.IsDefault;
        domain.IsActive = request.IsActive ?? domain.IsActive;

        if (request.DefaultOg is not null)
        {
            domain.DefaultOg = ControlJson.WriteOgMeta(request.DefaultOg);
        }

        await domains.UpdateAsync(domain, cancellationToken);

        await AdministrativeAudit.RecordAsync(
            audit,
            caller,
            AuditActions.DomainUpdated,
            AuditActions.DomainSubject,
            domain.Id.ToString(),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["host"] = domain.Host },
            cancellationToken);

        // Consent, branding and Open Graph defaults all live in the cached domain configuration, so
        // any of them changing has to drop it — otherwise a tightened consent mode would keep being
        // ignored for as long as the entry lives (§E.6.2).
        await cache.InvalidateHostAsync(domain.Host, cancellationToken);

        return TypedResults.Ok(Project(domain));
    }

    /// <summary>Removes a domain.</summary>
    /// <param name="id">The domain identifier.</param>
    /// <param name="http">The request, for the authenticated caller.</param>
    /// <param name="domains">Domain storage.</param>
    /// <param name="audit">The administrative audit trail (FR-246).</param>
    /// <param name="cache">Drops the cached configuration the edge holds for the host.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>204, 404, or 409 when links still hang off the domain.</returns>
    public static async Task<IResult> DeleteAsync(
        Guid id,
        HttpContext http,
        DomainRepository domains,
        AuditLogWriter audit,
        ILinkCacheInvalidator cache,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(domains);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(cache);

        DleCaller caller = http.RequireDleCaller();

        LinkDomain? domain = await domains.GetAsync(id, cancellationToken);

        if (domain is null)
        {
            return DleProblem.NotFound("There is no domain with that identifier.");
        }

        try
        {
            if (!await domains.RemoveAsync(id, cancellationToken))
            {
                return DleProblem.NotFound("There is no domain with that identifier.");
            }
        }
        catch (DbUpdateException exception) when (IsForeignKeyViolation(exception))
        {
            // Links still point at it. Deleting the domain would orphan short URLs that are in
            // circulation, so the operator is told to deal with the links first rather than having
            // them silently removed.
            return DleProblem.Conflict(
                ProblemCodes.Base + "domain-in-use",
                "The domain still serves links.",
                "Delete or move the links on this host before removing it. Short URLs already in "
                + "circulation would otherwise stop resolving with no trace of why.");
        }

        await AdministrativeAudit.RecordAsync(
            audit,
            caller,
            AuditActions.DomainDeleted,
            AuditActions.DomainSubject,
            id.ToString(),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["host"] = domain.Host },
            cancellationToken);

        await cache.InvalidateHostAsync(domain.Host, cancellationToken);

        return TypedResults.NoContent();
    }

    /// <summary>Projects a stored domain.</summary>
    /// <param name="domain">The stored row.</param>
    /// <returns>The representation.</returns>
    internal static DomainResponse Project(LinkDomain domain) => new()
    {
        Id = domain.Id,
        TenantId = domain.TenantId,
        Host = domain.Host,
        IsDefault = domain.IsDefault,
        IsActive = domain.IsActive,
        TlsStatus = domain.TlsStatus,
        AasaStatus = domain.AasaStatus,
        AssetlinksStatus = domain.AssetlinksStatus,
        LastVerifiedAt = domain.LastVerifiedAt,
        ConsentModeOverride = domain.ConsentModeOverride,
        CreatedAt = domain.CreatedAt,
    };

    /// <summary>
    /// Reads the consent mode override, refusing one that would widen the tenant's setting.
    /// </summary>
    /// <param name="requested">The value supplied; an empty string clears the override.</param>
    /// <param name="tenantMode">The tenant's stored consent mode.</param>
    /// <param name="stored">The value to store, or <see langword="null"/> for no override.</param>
    /// <param name="failure">The problem document when the value is unusable.</param>
    /// <returns><see langword="true"/> when the value may be stored.</returns>
    /// <remarks>
    /// A domain may only narrow what the tenant allows. Letting it widen would mean a per-host escape
    /// hatch from the tenant's own privacy decision, and the effective mode the consent gate computes
    /// is the narrower of the two anyway — so an override that claimed to widen would be silently
    /// ignored at resolve time, which is worse than being refused here (§E.6.2).
    /// </remarks>
    private static bool TryReadConsentOverride(
        string? requested,
        string tenantMode,
        out string? stored,
        out IResult? failure)
    {
        stored = null;
        failure = null;

        if (requested is null || requested.Length == 0)
        {
            return true;
        }

        if (!TryParseConsentMode(requested, out ConsentMode mode))
        {
            failure = DleProblem.Validation(
                "consent_mode_override",
                "The consent mode must be off, aggregate_only or full.");

            return false;
        }

        if (!TryParseConsentMode(tenantMode, out ConsentMode tenant))
        {
            tenant = ConsentMode.Off;
        }

        if (mode > tenant)
        {
            failure = DleProblem.Validation(
                "consent_mode_override",
                "A domain override may only tighten the tenant's consent mode, never widen it.");

            return false;
        }

        stored = requested.Trim().ToLowerInvariant();
        return true;
    }

    /// <summary>Reads a consent mode name.</summary>
    /// <param name="value">The name.</param>
    /// <param name="mode">The mode.</param>
    /// <returns><see langword="true"/> when the name is recognised.</returns>
    internal static bool TryParseConsentMode(string? value, out ConsentMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "off":
                mode = ConsentMode.Off;
                return true;
            case "aggregate_only":
                mode = ConsentMode.AggregateOnly;
                return true;
            case "full":
                mode = ConsentMode.Full;
                return true;
            default:
                mode = ConsentMode.Off;
                return false;
        }
    }

    /// <summary>Tells a unique violation from any other write failure.</summary>
    /// <param name="exception">The failure reported by EF Core.</param>
    /// <returns><see langword="true"/> when the failure is a unique violation.</returns>
    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgres
        && string.Equals(postgres.SqlState, UniqueViolation, StringComparison.Ordinal);

    /// <summary>Tells a foreign key violation from any other write failure.</summary>
    /// <param name="exception">The failure reported by EF Core.</param>
    /// <returns><see langword="true"/> when rows still reference the domain.</returns>
    private static bool IsForeignKeyViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgres
        && string.Equals(postgres.SqlState, "23503", StringComparison.Ordinal);
}
