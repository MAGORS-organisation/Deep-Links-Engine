using System.Globalization;
using System.Text;

using Dle.Domain.WellKnown;

using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Dle.Edge.WellKnown;

/// <summary>
/// Writes a generated association document to the response with the exact shape Apple and Google
/// accept, and answers a conditional request with 304.
/// </summary>
/// <remarks>
/// <para>
/// The response is assembled by hand rather than through <see cref="Results.Content(string, string?, Encoding?)"/>
/// because the content type has to be the bare string <c>application/json</c>. Passing an encoding to
/// the helper appends <c>; charset=utf-8</c>, and a charset parameter is exactly the kind of detail that
/// makes an association fail with no diagnostic anywhere (§A.2.1, TC-121). Writing
/// <see cref="HttpResponse.ContentType"/> directly is the only way to be certain of the header's bytes.
/// </para>
/// <para>
/// The document is never redirected, never negotiated and never varies on the query string. It varies
/// only on the host, which is part of the authority rather than the representation, so no
/// <c>Vary</c> header is emitted.
/// </para>
/// </remarks>
internal sealed class WellKnownJsonResult : IResult
{
    private readonly byte[] _payload;
    private readonly string _contentType;
    private readonly string _etag;
    private readonly EntityTagHeaderValue? _parsedETag;
    private readonly string _cacheControl;

    /// <summary>Creates the result.</summary>
    /// <param name="document">The generated document.</param>
    /// <param name="maxAgeSeconds">Value of the <c>max-age</c> directive; matches the server-side cache lifetime.</param>
    internal WellKnownJsonResult(WellKnownDocument document, int maxAgeSeconds)
    {
        ArgumentNullException.ThrowIfNull(document);

        _payload = Encoding.UTF8.GetBytes(document.Json);
        _contentType = document.ContentType;
        _etag = document.ETag;
        _parsedETag = EntityTagHeaderValue.TryParse(document.ETag, out EntityTagHeaderValue? parsed) ? parsed : null;
        _cacheControl = string.Create(
            CultureInfo.InvariantCulture,
            $"public, max-age={maxAgeSeconds}, stale-while-revalidate={maxAgeSeconds}");
    }

    /// <inheritdoc />
    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        HttpResponse response = httpContext.Response;
        IHeaderDictionary headers = response.Headers;

        headers.ETag = _etag;
        headers.CacheControl = _cacheControl;
        headers.XContentTypeOptions = "nosniff";

        if (IsNotModified(httpContext.Request))
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            return Task.CompletedTask;
        }

        response.StatusCode = StatusCodes.Status200OK;

        // Assigned verbatim. No charset parameter is appended, and none may be.
        response.ContentType = _contentType;
        response.ContentLength = _payload.Length;

        // Kestrel drops the body of a HEAD response on its own, so the same path serves both verbs.
        return response.Body.WriteAsync(_payload.AsMemory(), httpContext.RequestAborted).AsTask();
    }

    private bool IsNotModified(HttpRequest request)
    {
        StringValues ifNoneMatch = request.Headers.IfNoneMatch;

        if (StringValues.IsNullOrEmpty(ifNoneMatch))
        {
            return false;
        }

        if (!EntityTagHeaderValue.TryParseList(ifNoneMatch, out IList<EntityTagHeaderValue>? candidates))
        {
            // Unparseable validator: serve the full document. Guessing would risk a stale association
            // file living on a device for a week (§A.2.1).
            return false;
        }

        foreach (EntityTagHeaderValue candidate in candidates)
        {
            if (candidate.Tag.Equals("*", StringComparison.Ordinal))
            {
                return true;
            }

            // The generated validator is strong, so a weak comparison is the correct one for
            // If-None-Match per RFC 9110 section 13.1.2.
            if (_parsedETag is not null && candidate.Compare(_parsedETag, useStrongComparison: false))
            {
                return true;
            }
        }

        return false;
    }
}
