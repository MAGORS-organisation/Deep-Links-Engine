using System.Data;
using System.Data.Common;

using Dle.Persistence.Fast.Configuration;
using Dle.Persistence.Fast.Data;

namespace Dle.Persistence.Fast.WellKnown;

/// <summary>
/// Loads per-domain configuration and the applications a host is associated with, and hands them to
/// <see cref="WellKnownBuilder"/> (FR-141, FR-142, §C.3.3).
/// </summary>
/// <remarks>
/// <para>
/// Document generation itself is a pure function in the shared kernel; this type only supplies its
/// input. That split is what makes the association files testable without a database and without HTTP,
/// and it keeps the one rule that matters here in one place: when a host has no application of that
/// platform the builder returns <see langword="null"/> and the endpoint must answer <c>404</c>.
/// Serving an empty but well-formed document instead is TC-122 — Apple accepts it, caches it for about
/// a week, and the domain's universal links are dead for that week.
/// </para>
/// <para>
/// Android fingerprints are emitted with the Play App Signing certificates first. That ordering is the
/// point of FR-144: with Play App Signing the certificate that matters on a real device is the one
/// Google re-signs with, and publishing only the local upload certificate is the single most common
/// reason App Links work in a debug build and fail in production (TC-123). Upload certificates are
/// still emitted after them so that internally distributed builds keep working.
/// </para>
/// </remarks>
public sealed class DapperDomainConfigStore : IDomainConfigStore
{
    private const string IosAppsSql = """
        SELECT a.team_id,
               a.bundle_id,
               a.appclip_bundle_id
        FROM apps a
        JOIN app_domains ad ON ad.app_id = a.id
        JOIN domains d ON d.id = ad.domain_id
        WHERE d.host = @host::citext
          AND lower(a.platform) = 'ios'
          AND d.is_active
        ORDER BY a.bundle_id
        """;

    private const string AndroidAppsSql = """
        SELECT a.bundle_id,
               a.play_signing_fingerprints,
               a.cert_fingerprints
        FROM apps a
        JOIN app_domains ad ON ad.app_id = a.id
        JOIN domains d ON d.id = ad.domain_id
        WHERE d.host = @host::citext
          AND lower(a.platform) = 'android'
          AND d.is_active
        ORDER BY a.bundle_id
        """;

    // default_language is read out of the branding document rather than from a column of its own:
    // §B.5.2 and SHARED-KERNEL §12 give `domains` a branding jsonb and no language column, and the
    // interstitial's default language is branding, not routing.
    private const string DomainSql = """
        SELECT d.id,
               d.tenant_id,
               d.host,
               t.consent_mode              AS tenant_consent_mode,
               d.consent_mode_override     AS domain_consent_mode,
               d.branding::text            AS branding,
               d.branding ->> 'default_language' AS default_language,
               d.default_og::text          AS default_og,
               d.is_active
        FROM domains d
        JOIN tenants t ON t.id = d.tenant_id
        WHERE d.host = @host::citext
        """;

    private readonly DleReadDataSource _readDataSource;
    private readonly int _commandTimeoutSeconds;

    /// <summary>
    /// Creates the store.
    /// </summary>
    /// <param name="readDataSource">The read pool, which is a replica under §B.8 profile B.</param>
    /// <param name="options">Hot-path persistence options; supplies the command timeout.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public DapperDomainConfigStore(DleReadDataSource readDataSource, FastPersistenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(readDataSource);
        ArgumentNullException.ThrowIfNull(options);

        _readDataSource = readDataSource;
        _commandTimeoutSeconds = options.CommandTimeoutSeconds;
    }

    /// <inheritdoc />
    public async ValueTask<WellKnownDocument?> BuildAasaAsync(string host, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        var apps = new List<AasaAppEntry>();

        await using NpgsqlCommand command = CreateCommand(IosAppsSql, host);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult, ct);

        while (await reader.ReadAsync(ct))
        {
            string? teamId = PostgresValues.String(reader, 0);
            string? bundleId = PostgresValues.String(reader, 1);

            // An app ID is "<team id>.<bundle id>". Without a team identifier the entry cannot be
            // spelled at all, so the application is skipped rather than emitted in a broken form that
            // Apple would cache.
            if (string.IsNullOrWhiteSpace(teamId) || string.IsNullOrWhiteSpace(bundleId))
            {
                continue;
            }

            string? appClipBundleId = PostgresValues.String(reader, 2);

            apps.Add(new AasaAppEntry
            {
                AppId = string.Concat(teamId.Trim(), ".", bundleId.Trim()),
                AppClipAppId = string.IsNullOrWhiteSpace(appClipBundleId)
                    ? null
                    : string.Concat(teamId.Trim(), ".", appClipBundleId.Trim()),
                Components = WellKnownBuilder.DefaultComponents,
            });
        }

        return WellKnownBuilder.BuildAasa(apps);
    }

    /// <inheritdoc />
    public async ValueTask<WellKnownDocument?> BuildAssetLinksAsync(string host, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        var apps = new List<AndroidAppEntry>();

        await using NpgsqlCommand command = CreateCommand(AndroidAppsSql, host);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult, ct);

        while (await reader.ReadAsync(ct))
        {
            string? packageName = PostgresValues.String(reader, 0);

            if (string.IsNullOrWhiteSpace(packageName))
            {
                continue;
            }

            string[] fingerprints = Combine(
                PostgresValues.StringArray(reader, 1),
                PostgresValues.StringArray(reader, 2));

            if (fingerprints.Length == 0)
            {
                continue;
            }

            apps.Add(new AndroidAppEntry
            {
                PackageName = packageName.Trim(),
                Sha256CertFingerprints = fingerprints,
            });
        }

        return WellKnownBuilder.BuildAssetLinks(apps);
    }

    /// <inheritdoc />
    public async ValueTask<DomainRuntimeConfig?> GetDomainAsync(string host, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        await using NpgsqlCommand command = CreateCommand(DomainSql, host);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);

        return await reader.ReadAsync(ct) ? MapDomain(reader) : null;
    }

    private NpgsqlCommand CreateCommand(string sql, string host)
    {
        NpgsqlCommand command = _readDataSource.DataSource.CreateCommand(sql);
        command.CommandTimeout = _commandTimeoutSeconds;
        command.Parameters.Add(new NpgsqlParameter<string>("host", host));
        return command;
    }

    private static DomainRuntimeConfig MapDomain(DbDataReader reader) => new()
    {
        Id = reader.GetGuid(0),
        TenantId = reader.GetGuid(1),
        Host = reader.GetString(2),
        TenantConsentMode = PostgresValues.ParseConsentMode(PostgresValues.String(reader, 3), ConsentMode.Off),
        DomainConsentMode = ReadOptionalConsentMode(reader, 4),
        InterstitialBrandJson = PostgresValues.String(reader, 5),
        DefaultLanguage = PostgresValues.String(reader, 6),
        DefaultOg = ReadOgMeta(reader, 7),
        IsActive = reader.GetBoolean(8),
    };

    private static ConsentMode? ReadOptionalConsentMode(DbDataReader reader, int ordinal)
    {
        string? raw = PostgresValues.String(reader, ordinal);

        return raw is null ? null : PostgresValues.ParseConsentMode(raw, ConsentMode.Off);
    }

    private static OgMeta? ReadOgMeta(DbDataReader reader, int ordinal)
    {
        string? json = PostgresValues.String(reader, ordinal);

        return string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize(json, DleDomainJsonContext.Default.OgMeta);
    }

    /// <summary>
    /// Concatenates the Play App Signing fingerprints and the upload fingerprints, in that order and
    /// without duplicates. <see cref="WellKnownBuilder"/> normalises the spelling of each one.
    /// </summary>
    private static string[] Combine(string[] playSigning, string[] uploadCertificates)
    {
        if (playSigning.Length == 0 && uploadCertificates.Length == 0)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var combined = new List<string>(playSigning.Length + uploadCertificates.Length);

        foreach (string fingerprint in playSigning)
        {
            if (!string.IsNullOrWhiteSpace(fingerprint) && seen.Add(fingerprint.Trim()))
            {
                combined.Add(fingerprint.Trim());
            }
        }

        foreach (string fingerprint in uploadCertificates)
        {
            if (!string.IsNullOrWhiteSpace(fingerprint) && seen.Add(fingerprint.Trim()))
            {
                combined.Add(fingerprint.Trim());
            }
        }

        return [.. combined];
    }
}
