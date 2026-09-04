using Dle.Control.Features.Shared;
using Dle.Control.Identity;
using Dle.Control.Infrastructure;
using Dle.Crypto;

namespace Dle.Control.Features.ApiKeys;

/// <summary>
/// <c>GET</c>, <c>POST</c> and <c>DELETE /api/v1/api-keys</c> (FR-242).
/// </summary>
/// <remarks>
/// <para>
/// The secret is returned by the create call and by nothing else. Only an Argon2id hash is stored,
/// so a lost key is replaced rather than recovered, and a database dump yields no working
/// credentials. That is not an inconvenience to be worked around: an engine that could show a key a
/// second time would be an engine whose database contains keys.
/// </para>
/// <para>
/// A key carries a role and, optionally, a narrower scope list. The two are an AND, never an OR: the
/// role sets the ceiling and the scopes lower it, so a key with the admin role and the single scope
/// <c>links:read</c> can read links and nothing else — which is what makes a key issued to a
/// reporting job safe to leave in a scheduler.
/// </para>
/// </remarks>
public static class ManageApiKeys
{
    /// <summary>Longest key name accepted.</summary>
    private const int MaxNameLength = 200;

    /// <summary>Lists the tenant's keys, without their secrets.</summary>
    /// <param name="includeRevoked">Whether revoked keys are listed too.</param>
    /// <param name="keys">Key storage.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The keys, newest first.</returns>
    public static async Task<IResult> ListAsync(
        bool? includeRevoked,
        ApiKeyRepository keys,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);

        IReadOnlyList<ApiKey> rows =
            await keys.ListAsync(includeRevoked ?? false, cancellationToken);

        List<ApiKeyResponse> items = new(rows.Count);

        foreach (ApiKey row in rows)
        {
            items.Add(new ApiKeyResponse
            {
                Id = row.Id,
                Name = row.Name,
                Prefix = row.Prefix,
                Role = row.Role,
                Scopes = row.Scopes,
                LastUsedAt = row.LastUsedAt,
                ExpiresAt = row.ExpiresAt,
                RevokedAt = row.RevokedAt,
                CreatedAt = row.CreatedAt,
            });
        }

        return TypedResults.Ok(new PagedResponse<ApiKeyResponse>
        {
            Items = items,
            Total = items.Count,
        });
    }

    /// <summary>Issues a key (FR-242).</summary>
    /// <param name="request">The key to issue.</param>
    /// <param name="http">The request, for the authenticated caller.</param>
    /// <param name="keys">Key storage.</param>
    /// <param name="hasher">Mints the key and its Argon2id hash.</param>
    /// <param name="audit">The administrative audit trail (FR-246).</param>
    /// <param name="timeProvider">Clock, used to reject an expiry already in the past.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>201 with the secret, shown exactly once.</returns>
    /// <remarks>
    /// A caller may not issue a key more powerful than their own. Without that rule an editor could
    /// mint an owner key and hold every capability the role system was meant to withhold, which is
    /// privilege escalation through the credential endpoint rather than through the policies.
    /// </remarks>
    public static async Task<IResult> CreateAsync(
        CreateApiKeyRequest request,
        HttpContext http,
        ApiKeyRepository keys,
        ApiKeyHasher hasher,
        AuditLogWriter audit,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(timeProvider);

        DleCaller caller = http.RequireDleCaller();
        Dictionary<string, string[]> errors = new(StringComparer.Ordinal);

        string name = request.Name?.Trim() ?? string.Empty;

        if (name.Length == 0 || name.Length > MaxNameLength)
        {
            errors["name"] =
            [
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A name is required so that a key can be identified without revealing it, and "
                    + $"may be at most {MaxNameLength} characters."),
            ];
        }

        string role = request.Role?.Trim().ToLowerInvariant() ?? string.Empty;

        if (!DleRoles.IsKnown(role))
        {
            errors["role"] = ["The role must be owner, admin, editor or viewer."];
        }
        else if (DleRoles.Rank(role) > DleRoles.Rank(caller.Role))
        {
            errors["role"] =
            [
                "A key may not be issued with a role stronger than the role of the credential "
                + "issuing it.",
            ];
        }

        List<string> scopes = [];

        foreach (string scope in request.Scopes)
        {
            string candidate = scope?.Trim().ToLowerInvariant() ?? string.Empty;

            if (!DleScopes.IsGrantable(candidate))
            {
                errors["scopes"] =
                [
                    "One of the scopes is not a scope a control-plane key may be issued with. The "
                    + "SDK ingestion scope belongs to a different credential type and is never "
                    + "grantable here.",
                ];

                break;
            }

            if (!caller.HasScope(candidate))
            {
                errors["scopes"] =
                [
                    "A key may not be issued with a scope the issuing credential does not itself "
                    + "hold.",
                ];

                break;
            }

            scopes.Add(candidate);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        if (request.ExpiresAt is DateTimeOffset expiry && expiry <= now)
        {
            errors["expires_at"] = ["The expiry must be in the future."];
        }

        if (errors.Count > 0)
        {
            return DleProblem.Validation(errors, "The key cannot be issued as described.");
        }

        ApiKeyCredential credential = hasher.Create();

        ApiKey key = new()
        {
            TenantId = caller.TenantId,
            Name = name,
            Prefix = credential.Prefix,
            Hash = credential.Hash,
            Role = role,
            Scopes = scopes,
            ExpiresAt = request.ExpiresAt,
        };

        await keys.AddAsync(key, cancellationToken);

        // The entry records the prefix, never the secret. The prefix is what an operator recognises
        // in a listing and what appears in logs; the secret exists only in the response body of this
        // one call (SHARED-KERNEL §17.5).
        await AdministrativeAudit.RecordAsync(
            audit,
            caller,
            AuditActions.ApiKeyCreated,
            AuditActions.ApiKeySubject,
            key.Id.ToString(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["prefix"] = credential.Prefix,
                ["role"] = role,
                ["scopes"] = string.Join(',', scopes),
            },
            cancellationToken);

        return TypedResults.Created(
            string.Create(CultureInfo.InvariantCulture, $"/api/v1/api-keys/{key.Id}"),
            new ApiKeyCreatedResponse
            {
                Id = key.Id,
                Name = key.Name,
                Secret = credential.Token,
                Prefix = credential.Prefix,
                Role = key.Role,
                ExpiresAt = key.ExpiresAt,
            });
    }

    /// <summary>Revokes a key.</summary>
    /// <param name="id">The key identifier.</param>
    /// <param name="http">The request, for the authenticated caller.</param>
    /// <param name="keys">Key storage.</param>
    /// <param name="audit">The administrative audit trail (FR-246).</param>
    /// <param name="timeProvider">Clock used to stamp the revocation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>204, or 404.</returns>
    /// <remarks>
    /// The row is kept so that audit entries referring to it keep referring to something that exists;
    /// the soft-delete filter is what stops it authenticating anything again.
    /// </remarks>
    public static async Task<IResult> RevokeAsync(
        Guid id,
        HttpContext http,
        ApiKeyRepository keys,
        AuditLogWriter audit,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(timeProvider);

        DleCaller caller = http.RequireDleCaller();

        if (!await keys.RevokeAsync(id, timeProvider.GetUtcNow(), cancellationToken))
        {
            return DleProblem.NotFound("There is no key with that identifier.");
        }

        await AdministrativeAudit.RecordAsync(
            audit,
            caller,
            AuditActions.ApiKeyRevoked,
            AuditActions.ApiKeySubject,
            id.ToString(),
            metadata: null,
            cancellationToken);

        return TypedResults.NoContent();
    }
}
