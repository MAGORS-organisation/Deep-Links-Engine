using System.Buffers;
using Dle.Control.Configuration;

using Microsoft.Extensions.Options;

namespace Dle.Control.Infrastructure;

/// <summary>
/// The response headers the bundled administration console is served with.
/// </summary>
/// <remarks>
/// <para>
/// The console holds an owner-capable credential in the browser — in session storage by default, in
/// local storage when the operator chooses "remember on this device". The browser offers no storage a
/// script on the same origin cannot read, so the defence is to make sure no foreign script runs on
/// that origin: a Content-Security-Policy that allows scripts and styles only from the console's own
/// files (the Vite build emits no inline script), no framing, no plugins, and network access only to
/// the control plane itself and, when single sign-on is configured, the identity provider the PKCE
/// flow talks to.
/// </para>
/// <para>
/// <c>img-src</c> allows any https image because the link editor previews the Open Graph image an
/// editor typed in, which lives on the customer's own site.
/// </para>
/// <para>
/// Only the console's responses carry this policy. The API reference under <c>/scalar</c> is a
/// third-party page with its own needs and is not covered.
/// </para>
/// </remarks>
public static class AdminConsoleSecurityHeaders
{
    /// <summary>Path prefix the bundled console is served under.</summary>
    public const string PathPrefix = "/admin";

    private static readonly SearchValues<char> OriginDelimiterChars = SearchValues.Create(";,'\r\n\t ");

    /// <summary>Adds the console's security headers to a response that has not started yet.</summary>
    /// <param name="context">The request whose response is the console.</param>
    public static void Apply(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        IHeaderDictionary headers = context.Response.Headers;

        headers.ContentSecurityPolicy = BuildPolicy(context.RequestServices);
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
    }

    /// <summary>Whether a request path belongs to the console.</summary>
    /// <param name="path">The request path.</param>
    /// <returns><see langword="true"/> for <c>/admin</c> and its subpaths, or the root fallback console assets.</returns>
    public static bool IsConsolePath(PathString path) =>
        path.StartsWithSegments(PathPrefix, StringComparison.OrdinalIgnoreCase)
        || path.Equals("/", StringComparison.Ordinal);

    /// <summary>Builds the policy, including the identity provider's origin when one is configured.</summary>
    /// <param name="services">Request services.</param>
    /// <returns>The Content-Security-Policy value.</returns>
    internal static string BuildPolicy(IServiceProvider services)
    {
        string connect = "'self'";

        DleOidcOptions? oidc = services.GetService<IOptions<DleIdentityOptions>>()?.Value.Oidc;
        string? authority = oidc?.Authority;

        if (!string.IsNullOrWhiteSpace(authority)
            && Uri.TryCreate(authority, UriKind.Absolute, out Uri? issuer)
            && string.IsNullOrEmpty(issuer.UserInfo)
            && !string.IsNullOrEmpty(issuer.Host)
            && (issuer.Scheme == Uri.UriSchemeHttps
                || (issuer.Scheme == Uri.UriSchemeHttp && oidc is { RequireHttpsMetadata: false } && issuer.IsLoopback)))
        {
            string origin = issuer.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
            if (!string.IsNullOrEmpty(origin) && !origin.AsSpan().ContainsAny(OriginDelimiterChars))
            {
                connect += " " + origin;
            }
        }

        return "default-src 'self'; "
            + "script-src 'self'; "
            + "style-src 'self'; "
            + "img-src 'self' data: https:; "
            + "font-src 'self'; "
            + "connect-src " + connect + "; "
            + "object-src 'none'; "
            + "base-uri 'self'; "
            + "form-action 'self'; "
            + "frame-ancestors 'none'";
    }
}
