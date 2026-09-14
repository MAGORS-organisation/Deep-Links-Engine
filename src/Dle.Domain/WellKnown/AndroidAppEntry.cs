namespace Dle.Domain.WellKnown;

/// <summary>
/// One Android application registered on a link domain, as it will appear in <c>assetlinks.json</c>
/// (FR-142).
/// </summary>
public sealed record AndroidAppEntry
{
    /// <summary>The application package name, for example <c>sk.zakaznik.app</c>.</summary>
    public required string PackageName { get; init; }

    /// <summary>
    /// SHA-256 certificate fingerprints that may claim this domain.
    /// </summary>
    /// <remarks>
    /// With Play App Signing — the default for applications created since 2021 — the fingerprint that
    /// matters on real devices is the one of the certificate <em>Google</em> signs with, not the upload
    /// certificate from the local keystore. Using the upload certificate here is the single most common
    /// reason App Links work in a debug build and silently fail in production (§A.2.2, FR-144, TC-123).
    /// </remarks>
    public required IReadOnlyList<string> Sha256CertFingerprints { get; init; }

    /// <summary>
    /// Android 15+ dynamic link components (FR-142). When non-empty they are emitted under
    /// <c>relation_extensions</c>, which lets routing patterns change without shipping a new build.
    /// </summary>
    public IReadOnlyList<AasaComponent> DynamicComponents { get; init; } = [];
}
