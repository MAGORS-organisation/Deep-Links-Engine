using System.Globalization;

using Dle.Edge.Configuration;

using Microsoft.Extensions.Options;

namespace Dle.Edge.Resolution;

/// <summary>
/// Writes the response hardening headers on everything the edge serves (S-04, T-11, §E.7).
/// </summary>
/// <remarks>
/// <para>
/// The headers are written on the way <em>in</em>, before the rest of the pipeline runs, which is what
/// makes them the floor rather than the ceiling. A rendered page then replaces the content security
/// policy with its own, because only the page knows the per-response nonce that lets it carry an
/// inline stylesheet with no <c>unsafe-inline</c> anywhere; everything else — redirects, JSON, static
/// assets, problem documents, and any error the framework produces before a handler is reached — keeps
/// the policy set here.
/// </para>
/// <para>
/// That policy is the strictest one a response without markup can carry: <c>default-src 'none'</c>
/// denies every fetch directive instead of inheriting a permissive default, and the three directives
/// that are not fetch directives — <c>base-uri</c>, <c>form-action</c> and <c>frame-ancestors</c> —
/// are named explicitly because <c>default-src</c> does not cover them. On a 302 it costs three
/// hundred bytes and buys the guarantee that a response body somebody manages to inject into an error
/// path has nowhere to load a script from.
/// </para>
/// <para>
/// HSTS is written only for a request that arrived over TLS. A browser ignores it on plain HTTP by
/// specification, but a development host reached over <c>http://localhost</c> that also serves an
/// origin over TLS would otherwise pin itself, and un-pinning is a two-year wait.
/// </para>
/// </remarks>
public sealed class SecurityHeadersMiddleware
{
    /// <summary>
    /// Policy for a response that is not a rendered page: nothing may be fetched, nothing may frame it,
    /// nothing may be posted from it, and no base tag can re-root a relative URL.
    /// </summary>
    public const string NonDocumentContentSecurityPolicy =
        "default-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    /// <summary>Value of the <c>Referrer-Policy</c> header.</summary>
    /// <remarks>
    /// A resolve URL identifies the campaign. Sending it onward as a referrer would hand every target a
    /// log of which campaign delivered each visitor — a privacy leak and a competitive one — and
    /// SHARED-KERNEL §17.5 already forbids the edge from retaining the value itself.
    /// </remarks>
    public const string ReferrerPolicy = "no-referrer";

    /// <summary>Value of the <c>Permissions-Policy</c> header.</summary>
    public const string PermissionsPolicy =
        "accelerometer=(), camera=(), geolocation=(), gyroscope=(), microphone=(), payment=(), usb=()";

    private readonly RequestDelegate _next;
    private readonly SecurityHeaderOptions _options;
    private readonly string _strictTransportSecurity;

    /// <summary>Creates the middleware.</summary>
    /// <param name="next">The next component.</param>
    /// <param name="options">Header policy, bound from <c>Dle:Edge:Security</c>.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public SecurityHeadersMiddleware(RequestDelegate next, IOptions<SecurityHeaderOptions> options)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(options);

        _next = next;
        _options = options.Value;

        _strictTransportSecurity = string.Create(
            CultureInfo.InvariantCulture,
            $"max-age={_options.HstsMaxAgeSeconds}{(_options.HstsIncludeSubDomains ? "; includeSubDomains" : string.Empty)}");
    }

    /// <summary>Runs the middleware.</summary>
    /// <param name="context">The request.</param>
    /// <returns>A task that completes when the pipeline has finished.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_options.Enabled)
        {
            return _next(context);
        }

        IHeaderDictionary headers = context.Response.Headers;

        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = ReferrerPolicy;
        headers["Permissions-Policy"] = PermissionsPolicy;
        headers.ContentSecurityPolicy = NonDocumentContentSecurityPolicy;

        if (context.Request.IsHttps)
        {
            headers.StrictTransportSecurity = _strictTransportSecurity;
        }

        return _next(context);
    }
}
