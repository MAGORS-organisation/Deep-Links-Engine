using System.Text.Json;
using System.Text.Json.Serialization;

using Dle.Domain.Ports;

namespace Dle.Edge.Rendering;

/// <summary>
/// The tenant branding a rendered page may use, after validation (FR-163).
/// </summary>
/// <remarks>
/// Every member is optional and every member has already been checked against
/// <see cref="SafeUrl"/> by the time an instance exists. Nothing that reaches a page from this type can
/// carry a scheme, a colour syntax or a data URI that the renderer did not intend, which is what keeps
/// tenant-controlled configuration out of the T-11 threat surface.
/// </remarks>
internal sealed record PageBranding
{
    /// <summary>Branding with nothing configured.</summary>
    internal static PageBranding Empty { get; } = new();

    /// <summary>Product or company name shown next to the logo.</summary>
    internal string? ProductName { get; init; }

    /// <summary>Absolute <c>https</c> URL of the logo shown on the page.</summary>
    internal string? LogoUrl { get; init; }

    /// <summary>Base64 <c>data:</c> URI of the mark placed at the centre of a QR code.</summary>
    internal string? QrLogoDataUri { get; init; }

    /// <summary>Accent colour for the light scheme, as a hexadecimal literal.</summary>
    internal string? AccentColor { get; init; }

    /// <summary>Accent colour for the dark scheme, as a hexadecimal literal.</summary>
    internal string? AccentColorDark { get; init; }

    /// <summary>Absolute <c>https</c> URL of the tenant's support page.</summary>
    internal string? SupportUrl { get; init; }

    /// <summary>Absolute <c>https</c> URL of the tenant's privacy notice.</summary>
    internal string? PrivacyUrl { get; init; }

    /// <summary>Whether anything at all is configured.</summary>
    internal bool IsEmpty =>
        ProductName is null && LogoUrl is null && QrLogoDataUri is null &&
        AccentColor is null && AccentColorDark is null && SupportUrl is null && PrivacyUrl is null;

    /// <summary>
    /// Resolves the branding for one page from the configured source, the link domain and the instance
    /// defaults.
    /// </summary>
    /// <param name="brandingSource">Which branding the operator has selected.</param>
    /// <param name="domain">The link domain's runtime configuration, when the host is known.</param>
    /// <param name="options">The instance defaults.</param>
    /// <returns>Validated branding; never <see langword="null"/>.</returns>
    /// <remarks>
    /// <see cref="BrandingSource.Tenant"/> layers the tenant's values over the instance's field by
    /// field, so a tenant that configured only a logo still gets the operator's support and privacy
    /// links rather than none.
    /// </remarks>
    internal static PageBranding Resolve(BrandingSource brandingSource, DomainRuntimeConfig? domain, InterstitialOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        PageBranding instance = new()
        {
            ProductName = Trim(options.ProductName),
            LogoUrl = SafeUrl.Https(options.LogoUrl),
            SupportUrl = SafeUrl.Https(options.SupportUrl),
            PrivacyUrl = SafeUrl.Https(options.PrivacyUrl),
        };

        return brandingSource switch
        {
            BrandingSource.None => Empty,
            BrandingSource.Instance => instance,
            BrandingSource.Tenant => Parse(domain?.InterstitialBrandJson).LayeredOver(instance),

            // An unrecognised value must not silently widen what a page shows (SHARED-KERNEL §17.9).
            _ => Empty,
        };
    }

    /// <summary>
    /// Parses and validates the JSON stored on the link domain. Malformed configuration degrades to
    /// <see cref="Empty"/>; it never fails a request.
    /// </summary>
    /// <param name="json">The stored document, or <see langword="null"/>.</param>
    /// <returns>Validated branding.</returns>
    internal static PageBranding Parse(string? json)
    {
        const int maxJsonLength = 8 * 1024;

        if (string.IsNullOrWhiteSpace(json) || json.Length > maxJsonLength)
        {
            return Empty;
        }

        BrandingDocument? document;

        try
        {
            document = JsonSerializer.Deserialize(json, RenderingJsonContext.Default.BrandingDocument);
        }
        catch (JsonException)
        {
            // Branding is decoration. A tenant with a malformed document gets the neutral page rather
            // than a 500 on every click, and the control plane is where the value is validated on write.
            // The branch is not empty: it resolves to "no branding", the safe default.
            return Empty;
        }

        if (document is null)
        {
            return Empty;
        }

        return new PageBranding
        {
            ProductName = Trim(document.ProductName),
            LogoUrl = SafeUrl.Https(document.LogoUrl),
            QrLogoDataUri = SafeUrl.ImageDataUri(document.QrLogo),
            AccentColor = SafeUrl.HexColor(document.AccentColor),
            AccentColorDark = SafeUrl.HexColor(document.AccentColorDark),
            SupportUrl = SafeUrl.Https(document.SupportUrl),
            PrivacyUrl = SafeUrl.Https(document.PrivacyUrl),
        };
    }

    /// <summary>Fills every unset member of this instance from <paramref name="fallback"/>.</summary>
    /// <param name="fallback">Lower-priority branding.</param>
    /// <returns>A new instance.</returns>
    internal PageBranding LayeredOver(PageBranding fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);

        return new PageBranding
        {
            ProductName = ProductName ?? fallback.ProductName,
            LogoUrl = LogoUrl ?? fallback.LogoUrl,
            QrLogoDataUri = QrLogoDataUri ?? fallback.QrLogoDataUri,
            AccentColor = AccentColor ?? fallback.AccentColor,
            AccentColorDark = AccentColorDark ?? fallback.AccentColorDark,
            SupportUrl = SupportUrl ?? fallback.SupportUrl,
            PrivacyUrl = PrivacyUrl ?? fallback.PrivacyUrl,
        };
    }

    private static string? Trim(string? value)
    {
        const int maxNameLength = 64;

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();

        return trimmed.Length > maxNameLength ? trimmed[..maxNameLength] : trimmed;
    }

    /// <summary>Wire shape of the <c>branding</c> column on the link domain.</summary>
    internal sealed record BrandingDocument
    {
        /// <summary>Product or company name.</summary>
        [JsonPropertyName("product_name")]
        public string? ProductName { get; init; }

        /// <summary>Logo URL.</summary>
        [JsonPropertyName("logo_url")]
        public string? LogoUrl { get; init; }

        /// <summary>Base64 data URI of the QR centre mark.</summary>
        [JsonPropertyName("qr_logo")]
        public string? QrLogo { get; init; }

        /// <summary>Accent colour for the light scheme.</summary>
        [JsonPropertyName("accent_color")]
        public string? AccentColor { get; init; }

        /// <summary>Accent colour for the dark scheme.</summary>
        [JsonPropertyName("accent_color_dark")]
        public string? AccentColorDark { get; init; }

        /// <summary>Support page URL.</summary>
        [JsonPropertyName("support_url")]
        public string? SupportUrl { get; init; }

        /// <summary>Privacy notice URL.</summary>
        [JsonPropertyName("privacy_url")]
        public string? PrivacyUrl { get; init; }
    }
}
