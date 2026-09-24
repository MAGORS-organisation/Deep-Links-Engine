using Dle.Control.Features.Shared;
using Dle.Control.Identity;
using Dle.Control.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dle.Control.Features.Apps;

/// <summary>
/// <c>GET</c>, <c>POST</c>, <c>PATCH</c> and <c>DELETE /api/v1/apps</c> (FR-141, FR-142, FR-144).
/// </summary>
/// <remarks>
/// Neither the platform nor the bundle identifier can be edited. Both are part of the identity an
/// association file publishes, and changing one in place would leave every device that has already
/// cached the file pointing at an application that no longer claims the host (§A.2.1, §A.2.2).
/// </remarks>
public static class ManageApps
{
    /// <summary>Stored platform value for an iOS application.</summary>
    private const string IosPlatform = "ios";

    /// <summary>Stored platform value for an Android application.</summary>
    private const string AndroidPlatform = "android";

    private const string UniqueViolation = "23505";

    /// <summary>Lists the tenant's applications.</summary>
    /// <param name="apps">Application storage.</param>
    /// <param name="pairings">Reads which domains each application is paired with.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The applications.</returns>
    public static async Task<IResult> ListAsync(
        AppRepository apps,
        AppDomainPairings pairings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(pairings);

        IReadOnlyList<AppEntity> rows = await apps.ListAsync(cancellationToken);
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> domains =
            await pairings.ForTenantAsync(cancellationToken);

        List<AppResponse> items = new(rows.Count);

        foreach (AppEntity row in rows)
        {
            items.Add(Project(
                row,
                domains.TryGetValue(row.Id, out IReadOnlyList<Guid>? ids) ? ids : [],
                PlaySigningWarnings(row)));
        }

        return TypedResults.Ok(new PagedResponse<AppResponse>
        {
            Items = items,
            Total = items.Count,
        });
    }

    /// <summary>Reads one application.</summary>
    /// <param name="id">The application identifier.</param>
    /// <param name="apps">Application storage.</param>
    /// <param name="pairings">Reads which domains the application is paired with.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the application, or 404.</returns>
    public static async Task<IResult> GetAsync(
        Guid id,
        AppRepository apps,
        AppDomainPairings pairings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(pairings);

        AppEntity? app = await apps.GetAsync(id, cancellationToken);

        if (app is null)
        {
            return DleProblem.NotFound("There is no application with that identifier.");
        }

        IReadOnlyList<Guid> domains = await pairings.ForAppAsync(id, cancellationToken);

        return TypedResults.Ok(Project(app, domains, PlaySigningWarnings(app)));
    }

    /// <summary>Registers an application.</summary>
    /// <param name="request">The application to register.</param>
    /// <param name="http">The request, for the authenticated caller.</param>
    /// <param name="apps">Application storage.</param>
    /// <param name="pairings">Writes the domain pairings.</param>
    /// <param name="audit">The administrative audit trail (FR-246).</param>
    /// <param name="cache">Drops the cached association files of the paired hosts.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>201 with the application and any warnings, or a problem document.</returns>
    /// <remarks>
    /// An Android registration whose fingerprints do not look like Play App Signing fingerprints is
    /// accepted with a warning rather than refused. It is legitimate — a build installed straight
    /// from a developer machine is signed with the upload certificate — but it is also the single
    /// most common reason app links work in development and silently stop working in production, and
    /// the operator has to be told at the moment they can still act on it (FR-144, TC-123).
    /// </remarks>
    public static async Task<IResult> CreateAsync(
        CreateAppRequest request,
        HttpContext http,
        AppRepository apps,
        AppDomainPairings pairings,
        AuditLogWriter audit,
        ILinkCacheInvalidator cache,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(pairings);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(cache);

        DleCaller caller = http.RequireDleCaller();

        Dictionary<string, string[]> errors = new(StringComparer.Ordinal);
        string platform = request.Platform?.Trim().ToLowerInvariant() ?? string.Empty;

        if (platform is not (IosPlatform or AndroidPlatform))
        {
            errors["platform"] = ["The platform must be ios or android."];
        }

        if (string.IsNullOrWhiteSpace(request.BundleId))
        {
            errors["bundle_id"] = ["A bundle identifier or package name is required."];
        }

        if (platform == IosPlatform && string.IsNullOrWhiteSpace(request.TeamId))
        {
            errors["team_id"] =
            [
                "iOS needs the Apple team identifier: the association file publishes "
                + "<team>.<bundle>, and an entry without the team prefix makes iOS reject the whole "
                + "file rather than just that entry.",
            ];
        }

        List<string> fingerprints = NormalizeFingerprints(request.CertFingerprints, errors);

        if (errors.Count > 0)
        {
            return DleProblem.Validation(errors, "The application cannot be registered as described.");
        }

        AppEntity app = new()
        {
            TenantId = caller.TenantId,
            Platform = platform,
            BundleId = request.BundleId.Trim(),
            TeamId = Trim(request.TeamId),
            CertFingerprints = fingerprints,
            StoreId = Trim(request.StoreId),
            StoreUrl = Trim(request.StoreUrl),
            CustomScheme = Trim(request.CustomScheme)?.ToLowerInvariant(),
            MinAppVersion = Trim(request.MinAppVersion),
            AppClipBundleId = Trim(request.AppClipBundleId),
        };

        try
        {
            await apps.AddAsync(app, cancellationToken);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            // One registration per platform and bundle identifier in a tenant (uq_apps_tenant_platform_bundle).
            // The unique index is the arbiter; the caller is told to update the existing one.
            return DleProblem.Conflict(
                ProblemCodes.AppTaken,
                "The application is already registered.",
                "An application with this platform and bundle identifier already exists in this "
                + "tenant. Update that registration instead of adding a second one.");
        }

        IReadOnlyList<Guid> attached =
            await pairings.ReplaceAsync(app.Id, request.DomainIds, cancellationToken);

        await AdministrativeAudit.RecordAsync(
            audit,
            caller,
            AuditActions.AppCreated,
            AuditActions.AppSubject,
            app.Id.ToString(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["platform"] = app.Platform,
                ["bundle_id"] = app.BundleId,
            },
            cancellationToken);

        await InvalidatePairedHostsAsync(pairings, cache, attached, cancellationToken);

        return TypedResults.Created(
            string.Create(CultureInfo.InvariantCulture, $"/api/v1/apps/{app.Id}"),
            Project(app, attached, PlaySigningWarnings(app)));
    }

    /// <summary>Changes an application.</summary>
    /// <param name="id">The application identifier.</param>
    /// <param name="request">The fields to change.</param>
    /// <param name="http">The request, for the authenticated caller.</param>
    /// <param name="apps">Application storage.</param>
    /// <param name="pairings">Writes the domain pairings.</param>
    /// <param name="audit">The administrative audit trail (FR-246).</param>
    /// <param name="cache">Drops the cached association files of the paired hosts.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the application, or a problem document.</returns>
    public static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateAppRequest request,
        HttpContext http,
        AppRepository apps,
        AppDomainPairings pairings,
        AuditLogWriter audit,
        ILinkCacheInvalidator cache,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(pairings);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(cache);

        DleCaller caller = http.RequireDleCaller();

        AppEntity? app = await apps.GetAsync(id, cancellationToken);

        if (app is null)
        {
            return DleProblem.NotFound("There is no application with that identifier.");
        }

        Dictionary<string, string[]> errors = new(StringComparer.Ordinal);

        if (request.CertFingerprints is not null)
        {
            app.CertFingerprints = NormalizeFingerprints(request.CertFingerprints, errors);
        }

        if (request.PlaySigningFingerprints is not null)
        {
            app.PlaySigningFingerprints =
                NormalizeFingerprints(request.PlaySigningFingerprints, errors);
        }

        if (errors.Count > 0)
        {
            return DleProblem.Validation(errors, "The application cannot be changed as described.");
        }

        app.TeamId = request.TeamId is null ? app.TeamId : Trim(request.TeamId);
        app.StoreId = request.StoreId is null ? app.StoreId : Trim(request.StoreId);
        app.StoreUrl = request.StoreUrl is null ? app.StoreUrl : Trim(request.StoreUrl);
        app.CustomScheme = request.CustomScheme is null
            ? app.CustomScheme
            : Trim(request.CustomScheme)?.ToLowerInvariant();
        app.MinAppVersion = request.MinAppVersion is null ? app.MinAppVersion : Trim(request.MinAppVersion);
        app.AppClipBundleId = request.AppClipBundleId is null
            ? app.AppClipBundleId
            : Trim(request.AppClipBundleId);

        await apps.UpdateAsync(app, cancellationToken);

        IReadOnlyList<Guid> attached = request.DomainIds is null
            ? await pairings.ForAppAsync(app.Id, cancellationToken)
            : await pairings.ReplaceAsync(app.Id, request.DomainIds, cancellationToken);

        await AdministrativeAudit.RecordAsync(
            audit,
            caller,
            AuditActions.AppUpdated,
            AuditActions.AppSubject,
            app.Id.ToString(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["platform"] = app.Platform,
                ["bundle_id"] = app.BundleId,
            },
            cancellationToken);

        await InvalidatePairedHostsAsync(pairings, cache, attached, cancellationToken);

        return TypedResults.Ok(Project(app, attached, PlaySigningWarnings(app)));
    }

    /// <summary>Removes an application.</summary>
    /// <param name="id">The application identifier.</param>
    /// <param name="http">The request, for the authenticated caller.</param>
    /// <param name="apps">Application storage.</param>
    /// <param name="pairings">Reads the domains that will stop advertising it.</param>
    /// <param name="audit">The administrative audit trail (FR-246).</param>
    /// <param name="cache">Drops the cached association files of the paired hosts.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>204, or 404.</returns>
    public static async Task<IResult> DeleteAsync(
        Guid id,
        HttpContext http,
        AppRepository apps,
        AppDomainPairings pairings,
        AuditLogWriter audit,
        ILinkCacheInvalidator cache,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(pairings);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(cache);

        DleCaller caller = http.RequireDleCaller();

        IReadOnlyList<Guid> attached = await pairings.ForAppAsync(id, cancellationToken);

        if (!await apps.RemoveAsync(id, cancellationToken))
        {
            return DleProblem.NotFound("There is no application with that identifier.");
        }

        await AdministrativeAudit.RecordAsync(
            audit,
            caller,
            AuditActions.AppDeleted,
            AuditActions.AppSubject,
            id.ToString(),
            metadata: null,
            cancellationToken);

        await InvalidatePairedHostsAsync(pairings, cache, attached, cancellationToken);

        return TypedResults.NoContent();
    }

    /// <summary>Projects a stored application.</summary>
    /// <param name="app">The stored row.</param>
    /// <param name="domainIds">Domains the application is paired with.</param>
    /// <param name="warnings">Warnings that do not block registration.</param>
    /// <returns>The representation.</returns>
    private static AppResponse Project(
        AppEntity app,
        IReadOnlyList<Guid> domainIds,
        IReadOnlyList<string> warnings) => new()
        {
            Id = app.Id,
            TenantId = app.TenantId,
            Platform = app.Platform,
            BundleId = app.BundleId,
            TeamId = app.TeamId,
            CertFingerprints = app.CertFingerprints,
            StoreId = app.StoreId,
            StoreUrl = app.StoreUrl,
            CustomScheme = app.CustomScheme,
            AppClipBundleId = app.AppClipBundleId,
            DomainIds = domainIds,
            Warnings = warnings,
            CreatedAt = app.CreatedAt,
        };

    /// <summary>
    /// Produces the Play App Signing warning when a fingerprint does not look Play-signed
    /// (FR-144, TC-123).
    /// </summary>
    /// <param name="app">The application.</param>
    /// <returns>The warnings, empty when there is nothing to say.</returns>
    /// <remarks>
    /// The heuristic lives in <see cref="WellKnownValidator.LooksLikeUploadCertificate"/> so that the
    /// registration endpoint and the nightly association file validator reach the same verdict. An
    /// operator silences the warning by stating which fingerprints came from Play App Signing, which
    /// is the only way the engine can tell the two apart: both are ordinary SHA-256 digests.
    /// </remarks>
    private static List<string> PlaySigningWarnings(AppEntity app)
    {
        if (!string.Equals(app.Platform, AndroidPlatform, StringComparison.Ordinal))
        {
            return [];
        }

        if (app.CertFingerprints.Count == 0 && app.PlaySigningFingerprints.Count == 0)
        {
            return
            [
                "No signing certificate fingerprint is registered, so the Digital Asset Links file "
                + "for this application cannot be generated and Android app links will not verify "
                + "(FR-142).",
            ];
        }

        List<string> warnings = [];

        foreach (string fingerprint in app.CertFingerprints)
        {
            if (WellKnownValidator.LooksLikeUploadCertificate(fingerprint, app.PlaySigningFingerprints))
            {
                warnings.Add(
                    "The registered fingerprint does not match any fingerprint you have declared as "
                    + "coming from Play App Signing. If this is the upload certificate from your "
                    + "local keystore, app links will verify in a debug build and silently fail for "
                    + "every install from the Play Store. Copy the SHA-256 from Play Console under "
                    + "Setup, App signing, App signing key certificate, and record it as a Play "
                    + "signing fingerprint (FR-144).");

                break;
            }
        }

        return warnings;
    }

    /// <summary>
    /// Drops the cached association files of every host that advertises an application.
    /// </summary>
    /// <param name="pairings">Pairing storage, used to turn identifiers into hosts.</param>
    /// <param name="cache">The cache invalidator.</param>
    /// <param name="domainIds">The domains involved.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when every host has been invalidated.</returns>
    /// <remarks>
    /// Registering an application changes what <c>/.well-known/assetlinks.json</c> and the Apple
    /// association file contain for its hosts, and those documents are cached at the edge. Without
    /// this the change would not be visible until the entry expired (TC-125).
    /// </remarks>
    private static async Task InvalidatePairedHostsAsync(
        AppDomainPairings pairings,
        ILinkCacheInvalidator cache,
        IReadOnlyList<Guid> domainIds,
        CancellationToken cancellationToken)
    {
        if (domainIds.Count == 0)
        {
            return;
        }

        IReadOnlyList<string> hosts = await pairings.HostsAsync(domainIds, cancellationToken);

        foreach (string host in hosts)
        {
            await cache.InvalidateHostAsync(host, cancellationToken);
        }
    }

    /// <summary>
    /// Normalizes SHA-256 certificate fingerprints to the colon separated uppercase hex form.
    /// </summary>
    /// <param name="fingerprints">The values supplied.</param>
    /// <param name="errors">Field errors collected so far.</param>
    /// <returns>The normalized fingerprints.</returns>
    /// <remarks>
    /// Google prints the fingerprint with colons and Java prints it without, and an operator will
    /// paste whichever they have in front of them. Both forms are accepted and stored in one shape,
    /// so a comparison against the published association file never fails on punctuation.
    /// </remarks>
    private static List<string> NormalizeFingerprints(
        IReadOnlyList<string> fingerprints,
        Dictionary<string, string[]> errors)
    {
        List<string> normalized = new(fingerprints.Count);

        foreach (string raw in fingerprints)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string hex = raw.Replace(":", string.Empty, StringComparison.Ordinal)
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                .Trim()
                .ToUpperInvariant();

            if (hex.Length != 64 || !IsHex(hex))
            {
                errors["cert_fingerprints"] =
                [
                    "A signing certificate fingerprint is the SHA-256 digest: 64 hexadecimal "
                    + "characters, with or without colons.",
                ];

                return normalized;
            }

            char[] buffer = new char[95];
            int position = 0;

            for (int i = 0; i < 64; i += 2)
            {
                if (i > 0)
                {
                    buffer[position++] = ':';
                }

                buffer[position++] = hex[i];
                buffer[position++] = hex[i + 1];
            }

            normalized.Add(new string(buffer));
        }

        return normalized;
    }

    /// <summary>Whether every character is a hexadecimal digit.</summary>
    /// <param name="value">The candidate.</param>
    /// <returns><see langword="true"/> when the value is hexadecimal.</returns>
    private static bool IsHex(string value)
    {
        foreach (char c in value)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Trims a value, turning an empty result into <see langword="null"/>.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The trimmed value, or <see langword="null"/>.</returns>
    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
