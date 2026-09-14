using Dle.Domain.Contracts;
using Dle.Domain.Ports;
using Dle.Domain.Primitives;
using Dle.Edge.Rendering;
using Dle.Persistence.Fast.Caching;

using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

using QRCoder;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Maps <c>GET /{slug}/qr</c>, which renders a short link as a QR code (FR-106, §B.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>The endpoint does not look the link up.</b> It encodes <c>https://host/slug</c> for any slug the
/// naming policy accepts on a domain this instance serves, whether or not a link with that slug exists.
/// That is a security property, not a shortcut: the endpoint is unauthenticated and rate limited at
/// thirty requests a minute (§E.9), and answering 404 for an unknown slug would turn it into a slug
/// oracle that enumerates a tenant's links far more cheaply than the resolve path allows (T-07, TC-102,
/// TC-108). A code for a slug that does not resolve is harmless — it points at a 404 — and a code for a
/// link created a moment later works immediately.
/// </para>
/// <para>
/// <b>The payload is built, never received.</b> It is the request's own normalized host and the
/// normalized slug from the route, joined by <see cref="ShortLinkUrl"/>. No query parameter reaches it,
/// which is what stops the endpoint from becoming a generator of QR codes pointing at somebody else's
/// site under a trusted brand domain (SHARED-KERNEL §17.4).
/// </para>
/// </remarks>
public static class QrEndpointExtensions
{
    /// <summary>Route pattern of the endpoint.</summary>
    public const string RoutePattern = "/{slug}/qr";

    /// <summary>
    /// Name of the rate limiting policy §E.9 defines for this endpoint: a sliding window of thirty
    /// requests a minute keyed by address.
    /// </summary>
    /// <remarks>
    /// Passing it to <see cref="MapQr(IEndpointRouteBuilder, string?)"/> is opt-in because the policy is
    /// owned by the rate limiting module, and requiring a policy that was never registered fails the
    /// request at runtime rather than at startup. A deployment that limits by path in a global limiter
    /// leaves the argument unset.
    /// </remarks>
    public const string DefaultRateLimitPolicyName = "dle-edge-qr";

    /// <summary>How long a domain's runtime configuration is held for this endpoint, in minutes.</summary>
    private const int DomainCacheMinutes = 15;

    private static readonly HybridCacheEntryOptions DomainEntryOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(DomainCacheMinutes),
        LocalCacheExpiration = TimeSpan.FromMinutes(DomainCacheMinutes),
    };

    /// <summary>
    /// Maps <c>GET /{slug}/qr</c> and its <c>HEAD</c> counterpart.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <param name="rateLimitPolicyName">
    /// Name of a registered rate limiting policy to require on the endpoint, or <see langword="null"/>
    /// when limiting is applied globally. See <see cref="DefaultRateLimitPolicyName"/>.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    public static IEndpointRouteBuilder MapQr(this IEndpointRouteBuilder app, string? rateLimitPolicyName = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteHandlerBuilder route = app
            .MapMethods(RoutePattern, [HttpMethods.Get, HttpMethods.Head], GetQrAsync)
            .WithName("ResolveQrCode")
            .WithTags("qr")
            .WithSummary("QR code for a short link")
            .WithDescription(
                "Renders https://{host}/{slug} as a QR code. format=svg|png (default svg), " +
                "size=64..2048 pixels (default 512), ecc=L|M|Q|H (default M), logo=1|0 to include the " +
                "link domain's centre mark in the SVG. The link itself is not looked up, so the " +
                "endpoint is not a slug oracle; an unknown host answers 404.")
            .Produces(StatusCodes.Status200OK, contentType: QrCodeRenderer.SvgContentType)
            .Produces(StatusCodes.Status200OK, contentType: QrCodeRenderer.PngContentType)
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .AllowAnonymous();

        if (!string.IsNullOrWhiteSpace(rateLimitPolicyName))
        {
            route.RequireRateLimiting(rateLimitPolicyName);
        }

        return app;
    }

    /// <summary>Handles <c>GET /{slug}/qr</c>.</summary>
    /// <param name="slug">The slug from the route.</param>
    /// <param name="context">The request.</param>
    /// <param name="store">Source of the link domain's runtime configuration.</param>
    /// <param name="cache">The two-level cache the configuration is read through.</param>
    /// <param name="options">The page configuration, which decides whether tenant branding is used.</param>
    /// <param name="format">Requested image format.</param>
    /// <param name="size">Requested size in pixels.</param>
    /// <param name="ecc">Requested error correction level.</param>
    /// <param name="logo">Whether the centre mark is drawn.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The image, a 304, a 400 problem document, or 404 for a host this instance does not serve.</returns>
    private static async Task<IResult> GetQrAsync(
        string slug,
        HttpContext context,
        IDomainConfigStore store,
        HybridCache cache,
        IOptions<InterstitialOptions> options,
        string? format,
        int? size,
        string? ecc,
        string? logo,
        CancellationToken cancellationToken)
    {
        if (!HostNormalizer.TryNormalize(context.Request.Host.Host, out string host) ||
            !SlugPolicy.TryNormalize(slug, out string normalizedSlug) ||
            SlugPolicy.IsReserved(normalizedSlug))
        {
            return Results.NotFound();
        }

        if (!QrCodeRenderer.TryParseFormat(format, out QrFormat imageFormat))
        {
            return Invalid("format", "Supported values are svg and png.");
        }

        if (!QrCodeRenderer.TryParseEcc(ecc, out QRCodeGenerator.ECCLevel eccLevel))
        {
            return Invalid("ecc", "Supported values are L, M, Q and H.");
        }

        if (!TryParseFlag(logo, out bool includeLogo))
        {
            return Invalid("logo", "Supported values are 1, 0, true and false.");
        }

        DomainRuntimeConfig? domain = await cache.GetOrCreateAsync(
            LinkCacheKeys.Domain(host),
            (Store: store, Host: host),
            static (state, token) => state.Store.GetDomainAsync(state.Host, token),
            DomainEntryOptions,
            tags: [LinkCacheKeys.HostTag(host)],
            cancellationToken: cancellationToken);

        if (domain is null || !domain.IsActive)
        {
            // An unknown or disabled host serves nothing at all, exactly as it does for a slug.
            return Results.NotFound();
        }

        string payload = ShortLinkUrl.Build(host, normalizedSlug)!;

        InterstitialOptions pageOptions = options.Value;
        PageStrings strings = PageStrings.For(PrimaryLanguage(context.Request), domain, pageOptions);
        PageBranding branding = PageBranding.Resolve(pageOptions.Branding, domain, pageOptions);

        byte[] image = QrCodeRenderer.Render(
            payload,
            imageFormat,
            QrCodeRenderer.ClampSize(size),
            eccLevel,
            includeLogo ? branding.QrLogoDataUri : null,
            string.Format(CultureInfo.InvariantCulture, strings.QrAccessibleNameFormat, payload));

        return new QrImageResult(
            image,
            imageFormat is QrFormat.Png ? QrCodeRenderer.PngContentType : QrCodeRenderer.SvgContentType);
    }

    /// <summary>Builds the RFC 9457 document for a parameter the endpoint will not accept.</summary>
    private static IResult Invalid(string parameter, string detail) =>
        Results.Problem(
            detail: string.Concat("Query parameter '", parameter, "' is not valid. ", detail),
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid QR code request",
            type: ProblemCodes.ValidationFailed);

    /// <summary>
    /// Parses a boolean query flag. An absent value means "use the mark if there is one".
    /// </summary>
    /// <param name="value">The raw parameter.</param>
    /// <param name="flag">The parsed value.</param>
    /// <returns><see langword="false"/> for a value that is present and not recognised.</returns>
    private static bool TryParseFlag(string? value, out bool flag)
    {
        flag = true;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        string trimmed = value.Trim();

        if (trimmed is "1" || trimmed.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmed is "0" || trimmed.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            flag = false;
            return true;
        }

        // Anything else is a typo, and a typo that silently means "yes" is how a tenant ships a
        // thousand printed codes with a mark they asked not to have.
        return false;
    }

    /// <summary>
    /// Reads the primary language subtag from <c>Accept-Language</c>, for the image's accessible name.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The subtag, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Deliberately crude: the first tag wins and quality values are ignored. The value selects between
    /// two translations of a <c>title</c> element, which is not worth a full content negotiation, and
    /// the header is never stored or logged.
    /// </remarks>
    private static string? PrimaryLanguage(HttpRequest request)
    {
        const int maxHeaderLength = 256;

        StringValues header = request.Headers.AcceptLanguage;

        if (StringValues.IsNullOrEmpty(header))
        {
            return null;
        }

        string? first = header[0];

        if (string.IsNullOrWhiteSpace(first) || first.Length > maxHeaderLength)
        {
            return null;
        }

        ReadOnlySpan<char> span = first.AsSpan();
        int end = span.IndexOfAny(',', ';');

        return end < 0 ? first.Trim() : span[..end].Trim().ToString();
    }
}
