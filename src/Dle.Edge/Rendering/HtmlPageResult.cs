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
    private static string CreateNonce() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(NonceBytes));

    private static string BuildContentSecurityPolicy(string nonce) => string.Concat(
        "default-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'; ",
        "img-src 'self' https:; style-src 'self' 'nonce-" + nonce + "'; script-src 'nonce-" + nonce + "'; ",
        "font-src 'self'; connect-src 'none'; manifest-src 'none'; object-src 'none'; ",
        "require-trusted-types-for 'script'");
}
