using System.Security.Cryptography;

using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Dle.Edge.Rendering;

/// <summary>
/// Writes a rendered QR code with a strong validator and a long cache lifetime.
/// </summary>
/// <remarks>
/// <para>
/// The response is deliberately the most cacheable thing the edge serves. A QR code is a pure function
/// of the short URL, the requested parameters and the tenant's mark: it does not depend on the client,
/// it carries no identifier, and it is printed on things that are then scanned thousands of times. So it
/// is <c>public</c>, it is good for a day, and it carries a validator derived from the bytes themselves
/// so a revalidation costs 304 bytes rather than a re-render.
/// </para>
/// <para>
/// The document is served from the link domain's own origin, and an SVG served that way is a document
/// that could in principle execute script. Ours cannot — it is built from a fixed template, a module
/// matrix and two validated strings — and the policy below says so out loud anyway: no script source,
/// no style source, no origin to talk to, and images only from the <c>data:</c> URI the mark arrives in.
/// </para>
/// </remarks>
internal sealed class QrImageResult : IResult
{
    /// <summary>How long a rendered code stays fresh, in seconds.</summary>
    internal const int MaxAgeSeconds = 86_400;

    /// <summary>Bytes of the hash that become the entity tag.</summary>
    private const int ValidatorBytes = 16;

    private static readonly string CacheControlValue = string.Create(
        CultureInfo.InvariantCulture,
        $"public, max-age={MaxAgeSeconds}, stale-while-revalidate={MaxAgeSeconds}");

    private const string ContentSecurityPolicyValue =
        "default-src 'none'; img-src data:; style-src 'none'; script-src 'none'; " +
        "base-uri 'none'; form-action 'none'; frame-ancestors 'none'; sandbox";

    private readonly byte[] _payload;
    private readonly string _contentType;
    private readonly string _etag;
    private readonly EntityTagHeaderValue? _parsedETag;

    /// <summary>Creates the result.</summary>
    /// <param name="payload">The encoded image.</param>
    /// <param name="contentType">The image's content type.</param>
    internal QrImageResult(byte[] payload, string contentType)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrEmpty(contentType);

        _payload = payload;
        _contentType = contentType;
        _etag = CreateETag(payload);
        _parsedETag = EntityTagHeaderValue.TryParse(_etag, out EntityTagHeaderValue? parsed) ? parsed : null;
    }

    /// <inheritdoc />
    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        HttpResponse response = httpContext.Response;
        IHeaderDictionary headers = response.Headers;

        headers.ETag = _etag;
        headers.CacheControl = CacheControlValue;
        headers.XContentTypeOptions = "nosniff";
        headers.ContentSecurityPolicy = ContentSecurityPolicyValue;
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Resource-Policy"] = "cross-origin";

        if (IsNotModified(httpContext.Request))
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            return Task.CompletedTask;
        }

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = _contentType;
        response.ContentLength = _payload.Length;

        // Kestrel discards the body of a HEAD response, so one path serves both verbs.
        return response.Body.WriteAsync(_payload.AsMemory(), httpContext.RequestAborted).AsTask();
    }

    /// <summary>
    /// Derives a strong entity tag from the rendered bytes.
    /// </summary>
    /// <remarks>
    /// From the bytes rather than from the parameters, so that a change nobody thought of — a new
    /// tenant mark, a corrected renderer — invalidates the tag on its own. SHA-256 truncated to 128
    /// bits is a validator, not a secret; it is never compared against anything an attacker supplies
    /// for an authorisation decision.
    /// </remarks>
    private static string CreateETag(byte[] payload)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];

        SHA256.HashData(payload, hash);

        return string.Concat("\"", Convert.ToHexStringLower(hash[..ValidatorBytes]), "\"");
    }

    private bool IsNotModified(HttpRequest request)
    {
        StringValues ifNoneMatch = request.Headers.IfNoneMatch;

        if (StringValues.IsNullOrEmpty(ifNoneMatch) ||
            !EntityTagHeaderValue.TryParseList(ifNoneMatch, out IList<EntityTagHeaderValue>? candidates))
        {
            return false;
        }

        foreach (EntityTagHeaderValue candidate in candidates)
        {
            if (candidate.Tag.Equals("*", StringComparison.Ordinal))
            {
                return true;
            }

            if (_parsedETag is not null && candidate.Compare(_parsedETag, useStrongComparison: false))
            {
                return true;
            }
        }

        return false;
    }
}
