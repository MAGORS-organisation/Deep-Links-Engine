using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Options;

namespace Dle.Edge.Rendering;

/// <summary>
/// Base class of every HTML response the edge produces. Generates the per-response nonce, renders the
/// document with it and writes the security headers that make the nonce worth anything.
/// </summary>
/// <remarks>
/// <para>
/// The nonce is generated here rather than in the renderer because it has to appear in two places that
/// must agree: the <c>Content-Security-Policy</c> header and every <c>&lt;style&gt;</c> and
/// <c>&lt;script&gt;</c> element in the body. Splitting that responsibility is how a page ends up with
/// a policy that silently blocks its own stylesheet.
/// </para>
/// <para>
/// The policy has no <c>unsafe-inline</c> and no <c>unsafe-eval</c> anywhere, and
/// <c>default-src 'none'</c> means every fetch directive that is not listed is denied rather than
/// inherited from a permissive default (T-11, S-04). <c>form-action 'none'</c> matters more than it
/// looks: the pages contain no form, so any form an injection manages to create has nowhere to post to.
/// </para>
/// </remarks>
internal abstract class HtmlPageResult : IResult
{
    private const int NonceBytes = 16;

    private readonly SortedSet<string> _imageSources = new(StringComparer.Ordinal);

    /// <summary>HTTP status code of the response.</summary>
    protected abstract int StatusCode { get; }

    /// <summary>
    /// Value of the <c>Cache-Control</c> header. Defaults to <c>no-store</c>: an interstitial carries a
    /// click id and a claim code, and a status page must not outlive the condition that produced it.
    /// </summary>
    protected virtual string CacheControl => "no-store";

    /// <summary>Value of the <c>Retry-After</c> header, when the response has one.</summary>
    protected virtual TimeSpan? RetryAfter => null;

    /// <summary>Renders the document.</summary>
    /// <param name="nonce">The nonce this response's inline style and script must carry.</param>
    /// <param name="options">The resolved page configuration.</param>
    /// <returns>The complete HTML document.</returns>
    protected abstract string Render(string nonce, InterstitialOptions options);

    /// <inheritdoc />
    /// <remarks>
    /// Virtual so that a page with a configuration kill switch can answer differently without
    /// duplicating the header policy. Only <see cref="InterstitialPageResult"/> overrides it, and only
    /// to degrade to the ordinary 302 when the operator has switched the interstitial off.
    /// </remarks>
    public virtual async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        InterstitialOptions options = ResolveOptions(httpContext);

        string nonce = CreateNonce();
        byte[] payload = Encoding.UTF8.GetBytes(Render(nonce, options));

        HttpResponse response = httpContext.Response;
        IHeaderDictionary headers = response.Headers;

        response.StatusCode = StatusCode;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength = payload.Length;

        // Built after Render, which is what collected the image sources: the policy describes the
        // page that was actually produced rather than every page this type could ever produce.
        headers.ContentSecurityPolicy = BuildContentSecurityPolicy(nonce);
        headers.CacheControl = CacheControl;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "accelerometer=(), camera=(), geolocation=(), gyroscope=(), microphone=(), payment=(), usb=()";

        if (RetryAfter is { } retryAfter)
        {
            long seconds = Math.Max(1, (long)Math.Ceiling(retryAfter.TotalSeconds));
            headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }

        await response.Body.WriteAsync(payload.AsMemory(), httpContext.RequestAborted);
    }

    /// <summary>
    /// Resolves the page configuration from the request's services, falling back to the defaults.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <returns>The configuration; never <see langword="null"/>.</returns>
    /// <remarks>
    /// The fallback is not defensive noise. These results are constructed by static factory methods
    /// that the resolve pipeline calls without a service provider in hand, and a host that never bound
    /// <c>Dle:Edge:Interstitial</c> must still render a correct page rather than throw on a null
    /// options object.
    /// </remarks>
    private protected static InterstitialOptions ResolveOptions(HttpContext httpContext) =>
        httpContext.RequestServices?.GetService<IOptions<InterstitialOptions>>()?.Value ?? new InterstitialOptions();

    /// <summary>
    /// Generates the per-response nonce: 128 random bits in unpadded base64url.
    /// </summary>
    /// <returns>Exactly 22 characters from <c>[A-Za-z0-9_-]</c>.</returns>
    /// <remarks>
    /// <para>
    /// Base64url rather than base64, and that is not cosmetic. Plain base64 emits <c>+</c>, <c>/</c> and
    /// <c>=</c>, and an HTML encoder is free to escape some of those and not others; the nonce would then
    /// occupy a different number of bytes in the document from one response to the next. On the 404 page
    /// that difference is the whole ballgame: TC-102 and TC-166 require a missing slug and another
    /// tenant's slug to be indistinguishable, and a <c>Content-Length</c> that wobbles by a few bytes is
    /// exactly the kind of signal an attacker measures. With this alphabet every character survives
    /// encoding unchanged, so the rendered length is a function of the page alone.
    /// </para>
    /// <para>
    /// The alphabet is legal in a policy: the <c>base64-value</c> production of Content Security Policy
    /// Level 3 admits <c>-</c> and <c>_</c> alongside <c>+</c> and <c>/</c>, and the padding is optional.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Resolves the branding for this page and allows the images it brings with it.
    /// </summary>
    /// <param name="options">The interstitial options.</param>
    /// <param name="domain">The domain whose branding overrides the instance default.</param>
    /// <returns>The branding to render with.</returns>
    /// <remarks>
    /// Every page resolves its branding through here rather than calling
    /// <see cref="PageBranding.Resolve"/> itself, so that a logo can never be rendered into a page
    /// whose policy does not allow it. The two are one step, and they cannot drift apart.
    /// </remarks>
    private protected PageBranding ResolveBranding(InterstitialOptions options, DomainRuntimeConfig? domain)
    {
        ArgumentNullException.ThrowIfNull(options);

        PageBranding branding = PageBranding.Resolve(options.Branding, domain, options);

        AllowImage(branding.LogoUrl);
        AllowImage(branding.QrLogoDataUri);

        return branding;
    }

    /// <summary>
    /// Records that the page is about to reference an image, so the policy can name its origin.
    /// </summary>
    /// <param name="url">The image URL, or <see langword="null"/> when there is none.</param>
    /// <remarks>
    /// An absolute URL contributes its origin (scheme, host and port) and nothing else, so allowing
    /// a tenant's logo does not allow the rest of the web. A data URI contributes <c>data:</c>,
    /// which is the only way to allow an inline image at all. Anything that is neither is ignored:
    /// it would not have survived <see cref="SafeUrl"/> either.
    /// </remarks>
    private protected void AllowImage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            _ = _imageSources.Add("data:");
            return;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
            && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp))
        {
            _ = _imageSources.Add(parsed.GetLeftPart(UriPartial.Authority));
        }
    }

    private static string CreateNonce() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(NonceBytes));

    /// <summary>
    /// Builds the policy for the page that has just been rendered.
    /// </summary>
    /// <param name="nonce">The nonce the page's own style and script carry.</param>
    /// <returns>The header value.</returns>
    /// <remarks>
    /// <c>img-src</c> names the origins the page actually references and nothing more. It used to
    /// read <c>'self' https:</c>, which allowed every image on the web on every page, including the
    /// status pages that reference no remote image at all: a scheme is not a source, and an
    /// injected <c>&lt;img&gt;</c> pointing anywhere would have loaded and leaked the visit in its
    /// request. §E.8 S-04.
    /// </remarks>
    private string BuildContentSecurityPolicy(string nonce) => string.Concat(
        "default-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'; ",
        "img-src 'self'",
        _imageSources.Count == 0 ? string.Empty : " " + string.Join(' ', _imageSources),
        "; style-src 'self' 'nonce-" + nonce + "'; script-src 'nonce-" + nonce + "'; ",
        "font-src 'self'; connect-src 'none'; manifest-src 'none'; object-src 'none'; ",
        "require-trusted-types-for 'script'");
}
