using Dle.Control.Features.Domains;
using Dle.Control.Features.Shared;
using Dle.Control.Identity;
using Dle.Control.Infrastructure;

namespace Dle.Control.Features.Tenants;

/// <summary>
/// <c>GET</c>, <c>POST</c>, <c>PATCH</c> and <c>DELETE /api/v1/tenants</c> (FR-241).
/// </summary>
public static class ManageTenants
{
    /// <summary>Longest tenant name accepted.</summary>
    private const int MaxNameLength = 200;

    /// <summary>Most tenants returned in one listing.</summary>
    private const int ListLimit = 500;

    /// <summary>Reads the caller's own tenant.</summary>
    /// <param name="http">The request, for the authenticated caller.</param>
    /// <param name="tenants">Tenant storage, scoped to the caller's tenant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the tenant, or 404 when it has been deactivated.</returns>
    /// <remarks>
    /// Separate from the identifier route on purpose. Reading one's own tenant is an ordinary,
    /// viewer-level operation; reading somebody else's is instance administration, and conflating
    /// them would mean every viewer credential carried a route that could address another tenant.
    /// </remarks>
    public static async Task<IResult> GetOwnAsync(
        HttpContext http,
        TenantRepository tenants,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(tenants);

        DleCaller caller = http.RequireDleCaller();
        Tenant? tenant = await tenants.GetAsync(caller.TenantId, cancellationToken);

        return tenant is null
            ? DleProblem.NotFound("The tenant no longer exists.")
            : TypedResults.Ok(Project(tenant));
    }

    /// <summary>Lists every tenant on the instance.</summary>
    /// <param name="includeDeleted">Whether deactivated tenants are listed too.</param>
    /// <param name="tenants">Tenant storage.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tenants, oldest first.</returns>
    public static async Task<IResult> ListAsync(
        bool? includeDeleted,
        TenantRepository tenants,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenants);

        IReadOnlyList<Tenant> rows =
            await tenants.ListAsync(includeDeleted ?? false, ListLimit, cancellationToken);

        List<TenantResponse> items = new(rows.Count);

        foreach (Tenant row in rows)
        {
            items.Add(Project(row));
        }

        return TypedResults.Ok(new PagedResponse<TenantResponse>
        {
            Items = items,
            Total = items.Count,
        });
    }

    /// <summary>Reads one tenant.</summary>
    /// <param name="id">The tenant identifier.</param>
    /// <param name="administration">Cross-tenant tenant storage.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the tenant, or 404.</returns>
    public static async Task<IResult> GetAsync(
        Guid id,
        TenantAdministration administration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(administration);

        Tenant? tenant = await administration.GetAsync(id, includeDeleted: true, cancellationToken);

        return tenant is null
            ? DleProblem.NotFound("There is no tenant with that identifier.")
            : TypedResults.Ok(Project(tenant));
    }

    /// <summary>Provisions a tenant (FR-241).</summary>
    /// <param name="request">The tenant to create.</param>
    /// <param name="http">The request, for the authenticated caller.</param>
    /// <param name="tenants">Tenant storage.</param>
    /// <param name="administration">Cross-tenant tenant storage, for the slug uniqueness check.</param>
    /// <param name="audit">The administrative audit trail (FR-246).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>201 with the tenant, or a problem document.</returns>
    /// <remarks>
    /// The default consent mode is <c>aggregate_only</c>, the mode that needs no consent banner. A
    /// fresh tenant therefore never collects more than the legitimate interest basis supports until
    /// somebody deliberately widens it (§E.6.2, FR-248).
    /// </remarks>
    public static async Task<IResult> CreateAsync(
        CreateTenantRequest request,
        HttpContext http,
        TenantRepository tenants,
        TenantAdministration administration,
        AuditLogWriter audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(tenants);
        ArgumentNullException.ThrowIfNull(administration);
        ArgumentNullException.ThrowIfNull(audit);

        DleCaller caller = http.RequireDleCaller();
        Dictionary<string, string[]> errors = new(StringComparer.Ordinal);

        if (!SlugPolicy.TryNormalize(request.Slug, out string slug) || slug.Length < SlugPolicy.MinCustomLength)
        {
            errors["slug"] =
            [
                "A tenant slug is at least three characters of letters, digits and hyphens.",
            ];
        }

        string name = request.Name?.Trim() ?? string.Empty;

        if (name.Length == 0 || name.Length > MaxNameLength)
        {
            errors["name"] =
            [
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A display name is required and may be at most {MaxNameLength} characters."),
            ];
        }

        string consentMode = "aggregate_only";

        if (!string.IsNullOrWhiteSpace(request.ConsentMode))
        {
            if (!ManageDomains.TryParseConsentMode(request.ConsentMode, out ConsentMode parsed))
            {
                errors["consent_mode"] = ["The consent mode must be off, aggregate_only or full."];
            }
            else
            {
                consentMode = ConsentModeName(parsed);
            }
        }

        if (errors.Count > 0)
        {
            return DleProblem.Validation(errors, "The tenant cannot be provisioned as described.");
        }

        if (await administration.SlugExistsAsync(slug, cancellationToken))
        {
            return DleProblem.Conflict(
                ProblemCodes.Base + "tenant-slug-taken",
                "The tenant slug is already in use.",
                "Tenant slugs are unique across the instance and are never reused, because the audit "
                + "trail refers to them.");
        }

        Tenant tenant = await tenants.CreateAsync(
            new Tenant
            {
                Slug = slug,
                Name = name,
                Status = TenantStatuses.Active,
                ConsentMode = consentMode,
            },
            cancellationToken);

        await AdministrativeAudit.RecordAsync(
            audit,
            caller,
            AuditActions.TenantCreated,
            AuditActions.TenantSubject,
            tenant.Id.ToString(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["slug"] = tenant.Slug,
                ["consent_mode"] = tenant.ConsentMode,
            },
            cancellationToken);

        return TypedResults.Created(
            string.Create(CultureInfo.InvariantCulture, $"/api/v1/tenants/{tenant.Id}"),
            Project(tenant));
    }

    /// <summary>Changes a tenant.</summary>
    /// <param name="id">The tenant identifier.</param>
    /// <param name="request">The fields to change.</param>
    /// <param name="http">The request, for the authenticated caller.</param>
    /// <param name="administration">Cross-tenant tenant storage.</param>
    /// <param name="audit">The administrative audit trail (FR-246).</param>
    /// <param name="cache">Drops the cached configuration of every host of the tenant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the tenant, or a problem document.</returns>
    /// <remarks>
    /// A consent mode change is recorded with the old and the new value. Tightening takes effect on
    /// the next resolve; widening does not retroactively make already collected data lawful, and the
    /// audit entry is what lets that distinction be reconstructed later (§E.6.2).
    /// </remarks>
    public static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateTenantRequest request,
        HttpContext http,
        TenantAdministration administration,
        AuditLogWriter audit,
        ILinkCacheInvalidator cache,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(administration);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(cache);

        DleCaller caller = http.RequireDleCaller();

        Tenant? existing = await administration.GetAsync(id, includeDeleted: false, cancellationToken);

        if (existing is null)
        {
            return DleProblem.NotFound("There is no tenant with that identifier.");
        }

        string? name = null;

        if (request.Name is not null)
        {
            name = request.Name.Trim();

            if (name.Length == 0 || name.Length > MaxNameLength)
            {
                return DleProblem.Validation(
                    "name",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"A display name may be at most {MaxNameLength} characters."));
            }
        }

        string? consentMode = null;

        if (request.ConsentMode is not null)
        {
            if (!ManageDomains.TryParseConsentMode(request.ConsentMode, out ConsentMode parsed))
            {
                return DleProblem.Validation(
                    "consent_mode",
                    "The consent mode must be off, aggregate_only or full.");
            }

            consentMode = ConsentModeName(parsed);
        }

        string? status = null;

        if (request.Status is not null)
        {
            if (!TenantStatuses.IsSettable(request.Status))
            {
                return DleProblem.Validation(
                    "status",
                    "The status must be active or suspended. Deletion goes through DELETE, which is "
                    + "the operation the audit trail and the soft-delete filter are written around.");
            }

            status = request.Status.Trim().ToLowerInvariant();
        }

        Tenant? tenant = await administration.UpdateAsync(
            id,
            name,
            consentMode,
            status,
            cancellationToken);

        if (tenant is null)
        {
            return DleProblem.NotFound("There is no tenant with that identifier.");
        }

        Dictionary<string, string> metadata = new(StringComparer.Ordinal)
        {
            ["slug"] = tenant.Slug,
        };

        if (consentMode is not null)
        {
            metadata["consent_mode_from"] = existing.ConsentMode;
            metadata["consent_mode_to"] = consentMode;
        }

        if (status is not null)
        {
            metadata["status_from"] = existing.Status;
            metadata["status_to"] = status;
        }

        await AdministrativeAudit.RecordAsync(
            audit,
            caller,
            AuditActions.TenantUpdated,
            AuditActions.TenantSubject,
            tenant.Id.ToString(),
            metadata,
            cancellationToken);

        if (consentMode is not null)
        {
            // The effective consent mode is baked into every cached link snapshot, so a change has to
            // drop them all. A tightened mode that kept being ignored until the cache expired would
            // be a privacy setting that does not take effect when it is set (§E.6.2, TC-145).
            await cache.InvalidateTenantAsync(tenant.Id, cancellationToken);
        }

        return TypedResults.Ok(Project(tenant));
    }

    /// <summary>Deactivates a tenant.</summary>
    /// <param name="id">The tenant identifier.</param>
    /// <param name="http">The request, for the authenticated caller.</param>
    /// <param name="tenants">Tenant storage.</param>
    /// <param name="audit">The administrative audit trail (FR-246).</param>
    /// <param name="cache">Drops the cached configuration of every host of the tenant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>204, or 404.</returns>
    /// <remarks>
    /// The row stays. A hard delete would cascade through links, keys and the audit trail, and the
    /// audit trail is the one thing that has to outlive the objects it describes; the soft-delete
    /// filter is what stops the tenant appearing anywhere it should not.
    /// </remarks>
    public static async Task<IResult> DeleteAsync(
        Guid id,
        HttpContext http,
        TenantRepository tenants,
        AuditLogWriter audit,
        ILinkCacheInvalidator cache,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(tenants);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(cache);

        DleCaller caller = http.RequireDleCaller();

        if (!await tenants.SoftDeleteAsync(id, cancellationToken))
        {
            return DleProblem.NotFound("There is no tenant with that identifier.");
        }

        await AdministrativeAudit.RecordAsync(
            audit,
            caller,
            AuditActions.TenantDeleted,
            AuditActions.TenantSubject,
            id.ToString(),
            metadata: null,
            cancellationToken);

        await cache.InvalidateTenantAsync(id, cancellationToken);

        return TypedResults.NoContent();
    }

    /// <summary>Projects a stored tenant.</summary>
    /// <param name="tenant">The stored row.</param>
    /// <returns>The representation.</returns>
    private static TenantResponse Project(Tenant tenant) => new()
    {
        Id = tenant.Id,
        Slug = tenant.Slug,
        Name = tenant.Name,
        Status = tenant.Status,
        ConsentMode = tenant.ConsentMode,
        CreatedAt = tenant.CreatedAt,
    };

    /// <summary>Renders a consent mode as the name stored and returned.</summary>
    /// <param name="mode">The mode.</param>
    /// <returns>The name.</returns>
    private static string ConsentModeName(ConsentMode mode) => mode switch
    {
        ConsentMode.Full => "full",
        ConsentMode.AggregateOnly => "aggregate_only",
        _ => "off",
    };
}
