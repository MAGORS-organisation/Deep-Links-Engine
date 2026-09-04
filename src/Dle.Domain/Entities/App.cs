namespace Dle.Domain.Entities;

/// <summary>
/// A mobile application registered for deep linking (FR-141, FR-142). Maps to <c>apps</c>.
/// </summary>
public class App
{
    /// <summary>Primary key, a UUIDv7.</summary>
    public Guid Id { get; set; }

    /// <summary>Owning tenant.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Platform, stored as text: <c>ios</c> or <c>android</c>.</summary>
    public string Platform { get; set; } = string.Empty;

    /// <summary>Bundle identifier or package name, for example <c>com.example.app</c>.</summary>
    public string BundleId { get; set; } = string.Empty;

    /// <summary>Apple team identifier, for example <c>ABCDE12345</c>. iOS only.</summary>
    public string? TeamId { get; set; }

    /// <summary>SHA-256 certificate fingerprints declared by the customer. Android only.</summary>
    public List<string> CertFingerprints { get; set; } = [];

    /// <summary>
    /// SHA-256 fingerprints of the Play App Signing key. Kept separately from
    /// <see cref="CertFingerprints"/> because publishing the upload certificate instead of the
    /// signing certificate is the single most common reason Android app links silently stop
    /// working, and the validator can only warn about it if it can tell the two apart (FR-144,
    /// TC-123).
    /// </summary>
    public List<string> PlaySigningFingerprints { get; set; } = [];

    /// <summary>Store identifier, for example <c>id123456789</c> or the package name.</summary>
    public string? StoreId { get; set; }

    /// <summary>Custom URI scheme, used only as a last resort fallback (§A.2.3).</summary>
    public string? CustomScheme { get; set; }

    /// <summary>Lowest application version that understands the deep links of this tenant.</summary>
    public string? MinAppVersion { get; set; }

    /// <summary>Bundle identifier of the App Clip, when one is published (FR-146).</summary>
    public string? AppClipBundleId { get; set; }

    /// <summary>Store URL used when the application is not installed.</summary>
    public string? StoreUrl { get; set; }

    /// <summary>Creation instant, in UTC.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
