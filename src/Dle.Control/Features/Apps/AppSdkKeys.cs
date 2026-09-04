using Dle.Control.Features.Shared;
using Dle.Control.Identity;
using Dle.Control.Infrastructure;

using Microsoft.EntityFrameworkCore;

namespace Dle.Control.Features.Apps;

/// <summary>
/// <c>POST</c>, <c>GET</c> and <c>DELETE /api/v1/apps/{id}/sdk-keys</c> (§E.2.1, TB2).
/// </summary>
/// <remarks>
/// <para>
/// An SDK key ships inside an APK or an IPA, so it must be assumed extractable. Its value comes from
/// being bound to one application and one tenant and from being rate limited, not from being hidden.
/// What keeps an extracted key harmless is the authentication scheme it routes to: only the SDK
/// ingestion policy accepts that scheme, and no policy that writes configuration lists it. No
/// confusion of roles or scopes can change that, because authorization rejects the scheme before it
/// reads a claim.
/// </para>
/// <para>
/// The secret is nevertheless hashed with Argon2id and shown exactly once, like a control-plane key.
/// There is no reason for the weaker credential to have weaker cryptography, and it means a database
/// dump yields no working keys of either kind.
/// </para>
/// </remarks>
public static class AppSdkKeys
{
    /// <summary>Lists the keys issued for one application.</summary>
    /// <param name="id">The application identifier.</param>
    /// <param name="apps">Application storage, which scopes the lookup to the tenant.</param>
    /// <param name="db">The control-plane context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The keys, without their secrets.</returns>
    public static async Task<IResult> ListAsync(
        Guid id,
        AppRepository apps,
        DleDbContext db,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(db);

        if (await apps.GetAsync(id, cancellationToken) is null)
        {
            return DleProblem.NotFound("There is no application with that identifier.");
        }

        List<SdkKey> keys = await db.SdkKeys
            .AsNoTracking()
            .Where(k => k.AppId == id)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(cancellationToken);

        List<SdkKeyResponse> items = new(keys.Count);

        foreach (SdkKey key in keys)
        {
            items.Add(new SdkKeyResponse
            {
                Id = key.Id,
                AppId = key.AppId,
                Prefix = key.KeyPrefix,
                IsActive = key.IsActive,
                CreatedAt = key.CreatedAt,
            });
        }

        return TypedResults.Ok(new PagedResponse<SdkKeyResponse>
        {
            Items = items,
            Total = items.Count,
        });
    }

    /// <summary>Issues a key for one application.</summary>
    /// <param name="id">The application identifier.</param>
    /// <param name="http">The request, for the authenticated caller.</param>
    /// <param name="apps">Application storage, which scopes the lookup to the tenant.</param>
    /// <param name="db">The control-plane context.</param>
    /// <param name="factory">Mints the key.</param>
    /// <param name="audit">The administrative audit trail (FR-246).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>201 with the secret, shown exactly once.</returns>
    public static async Task<IResult> CreateAsync(
        Guid id,
        HttpContext http,
        AppRepository apps,
        DleDbContext db,
        DleSdkKeyFactory factory,
        AuditLogWriter audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(audit);

        DleCaller caller = http.RequireDleCaller();

        if (await apps.GetAsync(id, cancellationToken) is null)
        {
            return DleProblem.NotFound("There is no application with that identifier.");
        }

        (string token, string prefix, byte[] hash) = factory.Create();

        SdkKey key = new()
        {
            TenantId = caller.TenantId,
            AppId = id,
            KeyPrefix = prefix,
            Hash = hash,
            IsActive = true,
        };

        db.SdkKeys.Add(key);
        await db.SaveChangesAsync(cancellationToken);

        // The audit entry records the non-secret prefix, never the key. A trail that carried the
        // credential would turn every operator with read access to it into a holder of every key ever
        // issued (SHARED-KERNEL §17.5).
        await AdministrativeAudit.RecordAsync(
            audit,
            caller,
            AuditActions.SdkKeyCreated,
            AuditActions.SdkKeySubject,
            key.Id.ToString(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["app_id"] = id.ToString(),
                ["prefix"] = prefix,
            },
            cancellationToken);

        return TypedResults.Created(
            string.Create(CultureInfo.InvariantCulture, $"/api/v1/apps/{id}/sdk-keys/{key.Id}"),
            new SdkKeyCreatedResponse
            {
                Id = key.Id,
                AppId = id,
                Secret = token,
                Prefix = prefix,
                CreatedAt = key.CreatedAt,
            });
    }

    /// <summary>Revokes a key.</summary>
    /// <param name="id">The application identifier.</param>
    /// <param name="keyId">The key identifier.</param>
    /// <param name="http">The request, for the authenticated caller.</param>
    /// <param name="apps">Application storage, which scopes the lookup to the tenant.</param>
    /// <param name="db">The control-plane context.</param>
    /// <param name="audit">The administrative audit trail (FR-246).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>204, or 404.</returns>
    /// <remarks>
    /// The row is kept and switched off rather than deleted, so that the audit entries referring to
    /// it keep referring to something that exists. The authentication lookup requires
    /// <c>is_active</c>, so a revoked key stops authenticating immediately.
    /// </remarks>
    public static async Task<IResult> RevokeAsync(
        Guid id,
        Guid keyId,
        HttpContext http,
        AppRepository apps,
        DleDbContext db,
        AuditLogWriter audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);

        DleCaller caller = http.RequireDleCaller();

        if (await apps.GetAsync(id, cancellationToken) is null)
        {
            return DleProblem.NotFound("There is no application with that identifier.");
        }

        SdkKey? key = await db.SdkKeys
            .FirstOrDefaultAsync(k => k.Id == keyId && k.AppId == id, cancellationToken);

        if (key is null)
        {
            return DleProblem.NotFound("There is no key with that identifier.");
        }

        key.IsActive = false;
        await db.SaveChangesAsync(cancellationToken);

        await AdministrativeAudit.RecordAsync(
            audit,
            caller,
            AuditActions.SdkKeyRevoked,
            AuditActions.SdkKeySubject,
            key.Id.ToString(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["app_id"] = id.ToString(),
                ["prefix"] = key.KeyPrefix,
            },
            cancellationToken);

        return TypedResults.NoContent();
    }
}
