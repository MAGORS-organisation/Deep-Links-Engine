namespace Dle.Control.Features.Apps;

/// <summary>
/// Body of <c>PATCH /api/v1/apps/{id}</c>. Every member is optional; only the supplied ones change.
/// </summary>
/// <remarks>
/// Neither the platform nor the bundle identifier can be edited. Both are part of the identity an
/// association file publishes, and changing one in place would leave every device that has already
/// cached the file pointing at an application that no longer claims the host (§A.2.1, §A.2.2).
/// </remarks>
public sealed record UpdateAppRequest
{
    /// <summary>Apple team identifier, for example <c>ABCDE12345</c>.</summary>
    public string? TeamId { get; init; }

    /// <summary>
    /// Replacement SHA-256 signing certificate fingerprints. On Android these must be the Play
    /// Console App signing fingerprints, not the local upload keystore (FR-144, TC-123).
    /// </summary>
    public IReadOnlyList<string>? CertFingerprints { get; init; }

    /// <summary>
    /// Fingerprints known to come from Play App Signing. Supplying them here is what silences the
    /// upload-certificate warning, because it states which of the two the operator believes them
    /// to be.
    /// </summary>
    public IReadOnlyList<string>? PlaySigningFingerprints { get; init; }

    /// <summary>Store identifier, for example <c>id123456789</c> or the package name.</summary>
    public string? StoreId { get; init; }

    /// <summary>Full store URL used as the redirect target.</summary>
    public string? StoreUrl { get; init; }

    /// <summary>Custom URI scheme, used only as a last-resort fallback (§A.2.3).</summary>
    public string? CustomScheme { get; init; }

    /// <summary>Minimum application version that understands the deep link format.</summary>
    public string? MinAppVersion { get; init; }

    /// <summary>App Clip bundle identifier published in the association file (FR-146).</summary>
    public string? AppClipBundleId { get; init; }

    /// <summary>Replacement set of domains this application is associated with.</summary>
    public IReadOnlyList<Guid>? DomainIds { get; init; }
}

/// <summary>
/// Response to <c>POST /api/v1/apps/{id}/sdk-keys</c>.
/// </summary>
/// <remarks>
/// The secret appears here and nowhere else, exactly as for a control-plane key. Unlike one, this
/// key ships inside an APK or IPA and must be assumed extractable, which is why the scheme it
/// authenticates carries a policy that reaches only the two SDK ingestion routes and can never write
/// configuration (§E.2.1, TB2).
/// </remarks>
public sealed record SdkKeyCreatedResponse
{
    /// <summary>Key identifier.</summary>
    public required Guid Id { get; init; }

    /// <summary>Application the key is bound to.</summary>
    public required Guid AppId { get; init; }

    /// <summary>The secret, shown exactly once.</summary>
    public required string Secret { get; init; }

    /// <summary>Non-secret prefix, used to identify the key in listings and logs.</summary>
    public required string Prefix { get; init; }

    /// <summary>Creation instant.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Representation of an SDK key, without the secret.</summary>
public sealed record SdkKeyResponse
{
    /// <summary>Key identifier.</summary>
    public required Guid Id { get; init; }

    /// <summary>Application the key is bound to.</summary>
    public required Guid AppId { get; init; }

    /// <summary>Non-secret prefix.</summary>
    public required string Prefix { get; init; }

    /// <summary>Whether the key still authenticates.</summary>
    public required bool IsActive { get; init; }

    /// <summary>Creation instant.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}
