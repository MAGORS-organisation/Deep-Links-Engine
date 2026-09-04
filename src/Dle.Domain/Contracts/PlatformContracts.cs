namespace Dle.Domain.Contracts;

/// <summary>Body of <c>POST /api/v1/domains</c> (FR-145).</summary>
public sealed record CreateDomainRequest
{
    /// <summary>Host to serve links from, for example <c>link.customer.sk</c>. Each subdomain needs
    /// its own association file and its own entitlement; nothing is inherited (§A.2.1).</summary>
    public required string Host { get; init; }

    /// <summary>Make this the default domain for new links in the tenant.</summary>
    public bool IsDefault { get; init; }

    /// <summary>Consent mode override for this domain. It may only tighten the tenant setting.</summary>
    public string? ConsentModeOverride { get; init; }

    /// <summary>Default language for the interstitial page on this domain (NFR-15).</summary>
    public string? DefaultLanguage { get; init; }

    /// <summary>Default Open Graph metadata used when a link supplies none.</summary>
    public OgMeta? DefaultOg { get; init; }
}

/// <summary>Representation of a registered domain and its verification state.</summary>
public sealed record DomainResponse
{
    /// <summary>Domain identifier.</summary>
    public required Guid Id { get; init; }

    /// <summary>Owning tenant.</summary>
    public required Guid TenantId { get; init; }

    /// <summary>Normalized host.</summary>
    public required string Host { get; init; }

    /// <summary>Whether this is the tenant default.</summary>
    public required bool IsDefault { get; init; }

    /// <summary>Whether the domain currently serves links.</summary>
    public required bool IsActive { get; init; }

    /// <summary>TLS verification state: <c>pending</c>, <c>ok</c> or <c>failed</c>.</summary>
    public required string TlsStatus { get; init; }

    /// <summary>Apple association file state: <c>pending</c>, <c>ok</c> or <c>failed</c>.</summary>
    public required string AasaStatus { get; init; }

    /// <summary>Android association file state: <c>pending</c>, <c>ok</c> or <c>failed</c>.</summary>
    public required string AssetlinksStatus { get; init; }

    /// <summary>When verification last ran.</summary>
    public DateTimeOffset? LastVerifiedAt { get; init; }

    /// <summary>Consent mode override, when one is set.</summary>
    public string? ConsentModeOverride { get; init; }

    /// <summary>Creation instant.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Result of <c>POST /api/v1/domains/{id}/verify</c> (FR-143). The warning list matters as much as
/// the error list: an Android fingerprint taken from the local keystore instead of Play App Signing
/// verifies fine in a debug build and then fails silently in production (FR-144, TC-123).
/// </summary>
public sealed record DomainVerificationResponse
{
    /// <summary>Domain that was checked.</summary>
    public required Guid DomainId { get; init; }

    /// <summary>Host that was checked.</summary>
    public required string Host { get; init; }

    /// <summary>Whether every mandatory check passed.</summary>
    public required bool Ok { get; init; }

    /// <summary>When the check ran.</summary>
    public required DateTimeOffset CheckedAt { get; init; }

    /// <summary>Per check outcome.</summary>
    public IReadOnlyList<VerificationCheckResult> Checks { get; init; } = [];

    /// <summary>Operator facing reminder that association file changes take up to seven days to
    /// reach devices on both platforms (§A.2.1, §A.2.2, TC-125).</summary>
    public string? PropagationNotice { get; init; }
}

/// <summary>Outcome of one verification check.</summary>
public sealed record VerificationCheckResult
{
    /// <summary>What was checked: <c>dns</c>, <c>tls</c>, <c>aasa</c> or <c>assetlinks</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Result: <c>ok</c>, <c>warning</c> or <c>failed</c>.</summary>
    public required string Status { get; init; }

    /// <summary>Stable issue codes, for example <c>well_known.redirect</c>.</summary>
    public IReadOnlyList<string> Codes { get; init; } = [];

    /// <summary>Human readable explanation of what to fix.</summary>
    public string? Detail { get; init; }
}

/// <summary>Body of <c>POST /api/v1/apps</c>.</summary>
public sealed record CreateAppRequest
{
    /// <summary>Platform: <c>ios</c> or <c>android</c>.</summary>
    public required string Platform { get; init; }

    /// <summary>Bundle identifier or package name, for example <c>sk.customer.app</c>.</summary>
    public required string BundleId { get; init; }

    /// <summary>Apple team identifier, for example <c>ABCDE12345</c>. Required on iOS.</summary>
    public string? TeamId { get; init; }

    /// <summary>SHA-256 signing certificate fingerprints. On Android these must come from Play
    /// Console App signing, not from the local upload keystore (FR-144).</summary>
    public IReadOnlyList<string> CertFingerprints { get; init; } = [];

    /// <summary>Store identifier, for example <c>id123456789</c> or the package name.</summary>
    public string? StoreId { get; init; }

    /// <summary>Full store URL used as the redirect target.</summary>
    public string? StoreUrl { get; init; }

    /// <summary>Custom URI scheme, used only as a last resort fallback. Never carry secrets over
    /// it: any application can claim the same scheme (§A.2.3, CVE-2026-26123).</summary>
    public string? CustomScheme { get; init; }

    /// <summary>Minimum application version that understands the deep link format.</summary>
    public string? MinAppVersion { get; init; }

    /// <summary>App Clip bundle identifier, published in the association file (FR-146).</summary>
    public string? AppClipBundleId { get; init; }

    /// <summary>Domains this application is associated with.</summary>
    public IReadOnlyList<Guid> DomainIds { get; init; } = [];
}

/// <summary>Representation of a registered application.</summary>
public sealed record AppResponse
{
    /// <summary>Application identifier.</summary>
    public required Guid Id { get; init; }

    /// <summary>Owning tenant.</summary>
    public required Guid TenantId { get; init; }

    /// <summary>Platform.</summary>
    public required string Platform { get; init; }

    /// <summary>Bundle identifier or package name.</summary>
    public required string BundleId { get; init; }

    /// <summary>Apple team identifier.</summary>
    public string? TeamId { get; init; }

    /// <summary>Registered signing certificate fingerprints.</summary>
    public IReadOnlyList<string> CertFingerprints { get; init; } = [];

    /// <summary>Store identifier.</summary>
    public string? StoreId { get; init; }

    /// <summary>Store URL.</summary>
    public string? StoreUrl { get; init; }

    /// <summary>Custom URI scheme.</summary>
    public string? CustomScheme { get; init; }

    /// <summary>App Clip bundle identifier.</summary>
    public string? AppClipBundleId { get; init; }

    /// <summary>Associated domains.</summary>
    public IReadOnlyList<Guid> DomainIds { get; init; } = [];

    /// <summary>Warnings that do not block registration but will bite in production, most often the
    /// Play App Signing fingerprint mismatch.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Creation instant.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}
