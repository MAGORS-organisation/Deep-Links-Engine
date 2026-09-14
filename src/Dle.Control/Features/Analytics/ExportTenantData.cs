using System.IO.Compression;
using System.Text.Json;

using Dle.Control.Features.Shared;
using Dle.Control.Identity;
using Dle.Domain.Entities;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace Dle.Control.Features.Analytics;

/// <summary>
/// <c>GET /api/v1/exports/tenant</c> — everything the engine holds for one tenant, as one archive
/// (FR-249).
/// </summary>
/// <remarks>
/// <para>
/// This endpoint answers two requirements that are usually treated as chores and are in fact the
/// same product feature.
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     <b>GDPR portability.</b> Article 20 gives a controller the right to receive their data in a
///     structured, commonly used, machine readable format. Newline delimited JSON in a ZIP is
///     precisely that, and it is readable by every tool a customer already has.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>DORA article 30 exit plan.</b> A financial institution using this engine has to be able
///     to demonstrate that it can leave. §E.6.4 says to build that as a product feature and a sales
///     argument rather than as a support process, and this is it: the exit plan is an HTTP request,
///     not a project.
///     </description>
///   </item>
/// </list>
/// <para>
/// <b>What is never exported.</b> No Argon2id hash of an API key, no SDK key hash, no webhook
/// shared secret, no login key hash, no signing key material. A portable copy of every credential a
/// tenant has ever held, sitting in a file that by definition leaves the building, would be a worse
/// outcome than an export with a documented gap — and the manifest documents the gap so nobody has
/// to guess whether it is a bug.
/// </para>
/// <para>
/// The archive is written straight to the response as it is read from the database. A tenant with
/// ten million attributions must not become ten million rows in memory, so every table is streamed
/// row by row with no tracking and nothing is buffered but one entry's worth of JSON.
/// </para>
/// </remarks>
public static class ExportTenantData
{
    /// <summary>Version of the export layout, published in the manifest.</summary>
    public const string SchemaVersion = "1.0";

    /// <summary>Producer string published in the manifest.</summary>
    private const string Producer = "Deep Link Engine";

    /// <summary>
    /// Handles an export.
    /// </summary>
    /// <param name="context">The request, for the authenticated caller.</param>
    /// <param name="db">The control plane context.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the archive, or 404 when the tenant no longer exists.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static async Task<IResult> HandleAsync(
        HttpContext context,
        DleDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(timeProvider);

        DleCaller caller = context.RequireDleCaller();

        Tenant? tenant = await db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == caller.TenantId, cancellationToken);

        if (tenant is null)
        {
            return DleProblemResults.NotFound("The tenant does not exist.");
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        // ZipArchive writes synchronously and has no asynchronous surface, and Kestrel forbids
        // synchronous writes to a response body by default. Lifting the ban for this one request is
        // the documented way to stream an archive; the alternative is to build the whole archive in
        // memory first, which is exactly what an export of a tenant's entire history must not do.
        IHttpBodyControlFeature? bodyControl = context.Features.Get<IHttpBodyControlFeature>();

        if (bodyControl is not null)
        {
            bodyControl.AllowSynchronousIO = true;
        }

        string fileName = string.Create(
            CultureInfo.InvariantCulture,
            $"dle-export-{tenant.Slug}-{now:yyyyMMddHHmmss}.zip");

        return TypedResults.Stream(
            stream => WriteArchiveAsync(stream, db, tenant, now, cancellationToken),
            "application/zip",
            fileName);
    }

    /// <summary>Streams the whole archive into the response body.</summary>
    private static async Task WriteArchiveAsync(
        Stream destination,
        DleDbContext db,
        Tenant tenant,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // leaveOpen: the response body is owned by the server, not by the archive.
        using ZipArchive archive = new(destination, ZipArchiveMode.Create, leaveOpen: true);

        Dictionary<string, long> counts = new(StringComparer.Ordinal);

        counts["tenant.ndjson"] = await WriteEntryAsync(
            archive,
            "tenant.ndjson",
            OneAsync(tenant, cancellationToken),
            WriteTenant,
            cancellationToken);

        counts["domains.ndjson"] = await WriteEntryAsync(
            archive,
            "domains.ndjson",
            db.Domains.AsNoTracking().OrderBy(d => d.CreatedAt).AsAsyncEnumerable(),
            WriteDomain,
            cancellationToken);

        counts["apps.ndjson"] = await WriteEntryAsync(
            archive,
            "apps.ndjson",
            db.Apps.AsNoTracking().OrderBy(a => a.CreatedAt).AsAsyncEnumerable(),
            WriteApp,
            cancellationToken);

        counts["campaigns.ndjson"] = await WriteEntryAsync(
            archive,
            "campaigns.ndjson",
            db.Campaigns.AsNoTracking().OrderBy(c => c.CreatedAt).AsAsyncEnumerable(),
            WriteCampaign,
            cancellationToken);

        counts["links.ndjson"] = await WriteEntryAsync(
            archive,
            "links.ndjson",
            db.Links.AsNoTracking().OrderBy(l => l.Id).AsAsyncEnumerable(),
            WriteLink,
            cancellationToken);

        counts["webhooks.ndjson"] = await WriteEntryAsync(
            archive,
            "webhooks.ndjson",
            db.WebhookSubscriptions.AsNoTracking().OrderBy(w => w.CreatedAt).AsAsyncEnumerable(),
            WriteWebhook,
            cancellationToken);

        counts["api_keys.ndjson"] = await WriteEntryAsync(
            archive,
            "api_keys.ndjson",
            db.ApiKeys.AsNoTracking().OrderBy(k => k.CreatedAt).AsAsyncEnumerable(),
            WriteApiKey,
            cancellationToken);

        counts["installs.ndjson"] = await WriteEntryAsync(
            archive,
            "installs.ndjson",
            db.Installs.AsNoTracking().OrderBy(i => i.CreatedAt).AsAsyncEnumerable(),
            WriteInstall,
            cancellationToken);

        counts["attributions.ndjson"] = await WriteEntryAsync(
            archive,
            "attributions.ndjson",
            db.Attributions.AsNoTracking().OrderBy(a => a.MatchedAt).AsAsyncEnumerable(),
            WriteAttribution,
            cancellationToken);

        counts["abuse_reports.ndjson"] = await WriteEntryAsync(
            archive,
            "abuse_reports.ndjson",
            db.AbuseReports.AsNoTracking().OrderBy(r => r.CreatedAt).AsAsyncEnumerable(),
            WriteAbuseReport,
            cancellationToken);

        counts["audit_log.ndjson"] = await WriteEntryAsync(
            archive,
            "audit_log.ndjson",
            db.AuditLog.AsNoTracking().OrderBy(e => e.OccurredAt).AsAsyncEnumerable(),
            WriteAuditEntry,
            cancellationToken);

        await WriteManifestAsync(archive, tenant, now, counts, cancellationToken);
    }

    /// <summary>Writes the manifest entry.</summary>
    private static async Task WriteManifestAsync(
        ZipArchive archive,
        Tenant tenant,
        DateTimeOffset now,
        Dictionary<string, long> counts,
        CancellationToken cancellationToken)
    {
        TenantExportManifest manifest = new()
        {
            SchemaVersion = SchemaVersion,
            TenantId = tenant.Id,
            TenantSlug = tenant.Slug,
            GeneratedAt = now,
            Producer = Producer,
            Counts = counts,
            Omissions =
            [
                "api_keys.hash: the Argon2id hash of every control plane key is omitted. A key is "
                    + "replaced, never recovered.",
                "sdk_keys: omitted entirely, for the same reason. Re-issue them from the API.",
                "webhook_subscriptions.secret_encrypted: the shared signing secret is omitted. "
                    + "Rotate the subscription to obtain a new one.",
                "installs.login_key_hash: the keyed account hash is omitted. It is a pseudonymous "
                    + "identifier of an end user and is not part of a tenant's own data (§E.6.3).",
                "click_events: the raw click stream is not in this archive. It is partitioned, it is "
                    + "subject to the retention policy of FR-247, and it is exported through the "
                    + "analytics export endpoint in CSV or Parquet.",
                "signing_keys: engine key material, never a tenant's data.",
            ],
        };

        ZipArchiveEntry entry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);

        await using Stream stream = entry.Open();
        await using Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true });

        JsonSerializer.Serialize(writer, manifest, AnalyticsJsonContext.Default.TenantExportManifest);
        await writer.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Streams one table into one newline delimited JSON entry.
    /// </summary>
    /// <typeparam name="T">The row type.</typeparam>
    /// <param name="archive">The archive being written.</param>
    /// <param name="name">Entry name.</param>
    /// <param name="rows">The rows, read lazily.</param>
    /// <param name="write">Writes one row's members.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many rows were written.</returns>
    /// <remarks>
    /// Newline delimited rather than one large JSON array, so a consumer can process the file with
    /// a streaming reader and a producer never has to know the row count in advance. Both matter at
    /// the size these files reach.
    /// </remarks>
    private static async Task<long> WriteEntryAsync<T>(
        ZipArchive archive,
        string name,
        IAsyncEnumerable<T> rows,
        Action<Utf8JsonWriter, T> write,
        CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);

        await using Stream stream = entry.Open();
        await using Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = false });

        long written = 0;

        await foreach (T row in rows.WithCancellation(cancellationToken))
        {
            write(writer, row);
            await writer.FlushAsync(cancellationToken);

            // One JSON document per line. The writer is reset rather than recreated so the buffer
            // is reused across millions of rows.
            stream.WriteByte((byte)'\n');
            writer.Reset(stream);

            written++;
        }

        return written;
    }

    /// <summary>Yields a single row as an asynchronous sequence.</summary>
    private static async IAsyncEnumerable<T> OneAsync<T>(
        T value,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        yield return value;

        await Task.CompletedTask;
    }

    private static void WriteTenant(Utf8JsonWriter writer, Tenant tenant)
    {
        writer.WriteStartObject();
        writer.WriteString("id", tenant.Id);
        writer.WriteString("slug", tenant.Slug);
        writer.WriteString("name", tenant.Name);
        writer.WriteString("status", tenant.Status);
        writer.WriteString("consent_mode", tenant.ConsentMode);
        writer.WriteString("settings", tenant.Settings);
        writer.WriteString("created_at", tenant.CreatedAt);
        writer.WriteEndObject();
    }

    private static void WriteDomain(Utf8JsonWriter writer, LinkDomain domain)
    {
        writer.WriteStartObject();
        writer.WriteString("id", domain.Id);
        writer.WriteString("host", domain.Host);
        writer.WriteBoolean("is_default", domain.IsDefault);
        writer.WriteString("tls_status", domain.TlsStatus);
        writer.WriteString("aasa_status", domain.AasaStatus);
        writer.WriteString("assetlinks_status", domain.AssetlinksStatus);
        WriteNullableInstant(writer, "last_verified_at", domain.LastVerifiedAt);
        writer.WriteString("verification_log", domain.VerificationLog);
        WriteNullableString(writer, "consent_mode_override", domain.ConsentModeOverride);
        writer.WriteString("branding", domain.Branding);
        writer.WriteString("default_og", domain.DefaultOg);
        writer.WriteBoolean("is_active", domain.IsActive);
        writer.WriteString("created_at", domain.CreatedAt);
        writer.WriteEndObject();
    }

    private static void WriteApp(Utf8JsonWriter writer, AppEntity app)
    {
        writer.WriteStartObject();
        writer.WriteString("id", app.Id);
        writer.WriteString("platform", app.Platform);
        writer.WriteString("bundle_id", app.BundleId);
        WriteNullableString(writer, "team_id", app.TeamId);
        WriteStringArray(writer, "cert_fingerprints", app.CertFingerprints);
        WriteStringArray(writer, "play_signing_fingerprints", app.PlaySigningFingerprints);
        WriteNullableString(writer, "store_id", app.StoreId);
        WriteNullableString(writer, "custom_scheme", app.CustomScheme);
        WriteNullableString(writer, "min_app_version", app.MinAppVersion);
        WriteNullableString(writer, "appclip_bundle_id", app.AppClipBundleId);
        WriteNullableString(writer, "store_url", app.StoreUrl);
        writer.WriteString("created_at", app.CreatedAt);
        writer.WriteEndObject();
    }

    private static void WriteCampaign(Utf8JsonWriter writer, Campaign campaign)
    {
        writer.WriteStartObject();
        writer.WriteString("id", campaign.Id);
        writer.WriteString("name", campaign.Name);
        writer.WriteString("utm", campaign.Utm);
        writer.WriteString("created_at", campaign.CreatedAt);
        writer.WriteEndObject();
    }

    private static void WriteLink(Utf8JsonWriter writer, Link link)
    {
        writer.WriteStartObject();
        writer.WriteNumber("id", link.Id);
        writer.WriteString("domain_id", link.DomainId);
        writer.WriteString("slug", link.Slug);
        WriteNullableString(writer, "title", link.Title);
        WriteNullableString(writer, "description", link.Description);
        writer.WriteString("target_url", link.TargetUrl);
        WriteNullableString(writer, "deeplink_path", link.DeeplinkPath);
        writer.WriteString("routing_rules", link.RoutingRules);
        writer.WriteString("og_meta", link.OgMeta);
        writer.WriteString("utm", link.Utm);
        WriteNullableGuid(writer, "campaign_id", link.CampaignId);
        WriteStringArray(writer, "tags", link.Tags);
        writer.WriteBoolean("is_active", link.IsActive);
        WriteNullableInstant(writer, "starts_at", link.StartsAt);
        WriteNullableInstant(writer, "expires_at", link.ExpiresAt);
        WriteNullableString(writer, "expired_url", link.ExpiredUrl);
        WriteNullableInstant(writer, "quarantined_at", link.QuarantinedAt);
        writer.WriteString("created_at", link.CreatedAt);
        writer.WriteString("updated_at", link.UpdatedAt);
        writer.WriteNumber("version", link.Version);
        writer.WriteEndObject();
    }

    private static void WriteWebhook(Utf8JsonWriter writer, WebhookSubscription subscription)
    {
        writer.WriteStartObject();
        writer.WriteString("id", subscription.Id);
        writer.WriteString("url", subscription.Url);
        WriteStringArray(writer, "event_types", subscription.EventTypes);
        writer.WriteBoolean("is_active", subscription.IsActive);
        writer.WriteString("created_at", subscription.CreatedAt);

        // secret_encrypted is deliberately absent; see the manifest's omissions.
        writer.WriteEndObject();
    }

    private static void WriteApiKey(Utf8JsonWriter writer, ApiKey key)
    {
        writer.WriteStartObject();
        writer.WriteString("id", key.Id);
        writer.WriteString("name", key.Name);
        writer.WriteString("prefix", key.Prefix);
        writer.WriteString("role", key.Role);
        WriteStringArray(writer, "scopes", key.Scopes);
        WriteNullableInstant(writer, "last_used_at", key.LastUsedAt);
        WriteNullableInstant(writer, "expires_at", key.ExpiresAt);
        WriteNullableInstant(writer, "revoked_at", key.RevokedAt);
        writer.WriteString("created_at", key.CreatedAt);

        // hash is deliberately absent; see the manifest's omissions.
        writer.WriteEndObject();
    }

    private static void WriteInstall(Utf8JsonWriter writer, Install install)
    {
        writer.WriteStartObject();
        writer.WriteString("id", install.Id);
        writer.WriteString("app_id", install.AppId);
        writer.WriteString("install_id", install.InstallId);
        writer.WriteString("first_open_at", install.FirstOpenAt);
        WriteNullableString(writer, "raw_referrer", install.RawReferrer);
        writer.WriteString("platform", install.Platform);
        WriteNullableString(writer, "app_version", install.AppVersion);
        writer.WriteString("created_at", install.CreatedAt);

        // login_key_hash is deliberately absent; see the manifest's omissions.
        writer.WriteEndObject();
    }

    private static void WriteAttribution(Utf8JsonWriter writer, AttributionRecord record)
    {
        writer.WriteStartObject();
        writer.WriteString("id", record.Id);
        writer.WriteString("install_row_id", record.InstallId);
        WriteNullableString(writer, "click_id", record.ClickId);

        if (record.LinkId is { } linkId)
        {
            writer.WriteNumber("link_id", linkId);
        }

        writer.WriteString("match_type", record.MatchType);
        writer.WriteNumber("confidence", record.Confidence);
        writer.WriteString("matched_at", record.MatchedAt);

        if (record.WindowSeconds is { } window)
        {
            writer.WriteNumber("window_seconds", window);
        }

        writer.WriteString("evidence", record.Evidence);
        writer.WriteEndObject();
    }

    private static void WriteAbuseReport(Utf8JsonWriter writer, AbuseReport report)
    {
        writer.WriteStartObject();
        writer.WriteString("id", report.Id);
        writer.WriteNumber("link_id", report.LinkId);
        writer.WriteString("reason", report.Reason);
        WriteNullableString(writer, "details", report.Details);
        writer.WriteString("status", report.Status);
        writer.WriteString("created_at", report.CreatedAt);
        WriteNullableInstant(writer, "resolved_at", report.ResolvedAt);
        WriteNullableString(writer, "resolution_note", report.ResolutionNote);

        // reporter_email_hash is omitted: it identifies a reporter, who is not this tenant.
        writer.WriteEndObject();
    }

    private static void WriteAuditEntry(Utf8JsonWriter writer, AuditLogEntry entry)
    {
        writer.WriteStartObject();
        writer.WriteString("id", entry.Id);
        WriteNullableGuid(writer, "actor_id", entry.ActorId);
        writer.WriteString("actor_type", entry.ActorType);
        writer.WriteString("action", entry.Action);
        writer.WriteString("subject_type", entry.SubjectType);
        writer.WriteString("subject_id", entry.SubjectId);
        writer.WriteString("metadata", entry.Metadata);
        writer.WriteString("occurred_at", entry.OccurredAt);
        writer.WriteEndObject();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteNullableInstant(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value is { } instant)
        {
            writer.WriteString(name, instant);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteNullableGuid(Utf8JsonWriter writer, string name, Guid? value)
    {
        if (value is { } identifier)
        {
            writer.WriteString(name, identifier);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteStringArray(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);

        foreach (string value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }
}
