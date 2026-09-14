using Dle.Control.Configuration;
using Dle.Control.Features.Shared;

using Microsoft.Extensions.Options;

namespace Dle.Control.Features.Links;

/// <summary>
/// Turns a stored link into the representation the API returns (FR-101, FR-106).
/// </summary>
/// <remarks>
/// <para>
/// The short URL is composed here rather than stored, because it is derived: host plus slug, with
/// the scheme taken from configuration so that a development deployment behind plain HTTP still
/// produces URLs that resolve. Storing it would create a second copy of the truth that a domain
/// rename could silently invalidate — which is precisely why a domain cannot be renamed.
/// </para>
/// <para>
/// The QR URL points at the edge, not at the control plane. Rendering the image is a data-plane
/// concern with its own rate limit (§E.9), and the control plane's job is only to say where it
/// lives.
/// </para>
/// </remarks>
public sealed class LinkPresentation
{
    private readonly DleControlOptions _options;

    /// <summary>Creates the presenter.</summary>
    /// <param name="options">Control-plane options carrying the scheme and the QR path suffix.</param>
    public LinkPresentation(IOptions<DleControlOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    /// <summary>Composes the public short URL of a link.</summary>
    /// <param name="host">Host of the serving domain.</param>
    /// <param name="slug">Slug of the link.</param>
    /// <returns>The URL, ready to paste.</returns>
    public string ShortUrl(string host, string slug) => string.Create(
        CultureInfo.InvariantCulture,
        $"{_options.PublicScheme}://{host}/{slug}");

    /// <summary>Composes the URL of the QR image of a link (FR-106).</summary>
    /// <param name="host">Host of the serving domain.</param>
    /// <param name="slug">Slug of the link.</param>
    /// <returns>The URL of the image.</returns>
    public string QrUrl(string host, string slug) => string.Create(
        CultureInfo.InvariantCulture,
        $"{ShortUrl(host, slug)}{_options.QrPathSuffix}");

    /// <summary>Projects a stored link.</summary>
    /// <param name="link">The stored link.</param>
    /// <param name="host">Host of its serving domain.</param>
    /// <returns>The representation.</returns>
    public LinkResponse Project(Link link, string host)
    {
        ArgumentNullException.ThrowIfNull(link);

        return new LinkResponse
        {
            Id = link.Id.ToString(CultureInfo.InvariantCulture),
            TenantId = link.TenantId,
            DomainId = link.DomainId,
            Host = host,
            Slug = link.Slug,
            ShortUrl = ShortUrl(host, link.Slug),
            QrUrl = QrUrl(host, link.Slug),
            Title = link.Title,
            Description = link.Description,
            TargetUrl = link.TargetUrl,
            DeeplinkPath = link.DeeplinkPath,
            RoutingRules = ControlJson.ReadRoutingRules(link.RoutingRules),
            Og = ControlJson.ReadOgMeta(link.OgMeta),
            Utm = ControlJson.ReadStringMap(link.Utm),
            CampaignId = link.CampaignId,
            Tags = link.Tags,
            IsActive = link.IsActive,
            StartsAt = link.StartsAt,
            ExpiresAt = link.ExpiresAt,
            ExpiredUrl = link.ExpiredUrl,
            QuarantinedAt = link.QuarantinedAt,
            Version = link.Version,
            CreatedAt = link.CreatedAt,
            UpdatedAt = link.UpdatedAt,
        };
    }
}
