using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

using Dle.Control.Features.Jwks;
using Dle.Domain.Crypto;
using Dle.Domain.Serialization;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Publishes the public key set (SHARED-KERNEL §15).
/// </summary>
/// <remarks>
/// The endpoint is anonymous on purpose: a public key is public, and a verifier that had to
/// authenticate in order to fetch one could not verify anything it received out of band — which is
/// precisely the situation a webhook receiver is in (§B.7.4, T-13).
/// </remarks>
public static class JwksEndpointExtensions
{
    /// <summary>How long a cached key set stays fresh, in seconds.</summary>
    /// <remarks>
    /// Short enough that a key published now is picked up by a verifier within minutes, which is
    /// what makes a rotation with an overlap window safe (S-12); long enough that the endpoint is
    /// not a per-delivery round trip for a busy receiver.
    /// </remarks>
    private const int CacheSeconds = 300;

    /// <summary>Media type registered for a JWK Set by RFC 7517 §8.5.1.</summary>
    private const string JwkSetContentType = "application/jwk-set+json";

    /// <summary>
    /// Maps <c>GET /.well-known/jwks.json</c>.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The document lists every key still inside its validity window rather than only the key that
    /// currently signs. That is the whole mechanism behind rotation without an outage: the new key
    /// is published before it signs, the old key stays published until nothing signed under it can
    /// still be in flight, and a verifier that refreshes on an unknown <c>kid</c> never sees a gap.
    /// </remarks>
    public static IEndpointRouteBuilder MapJwks(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/.well-known/jwks.json", GetJwks)
            .AllowAnonymous()
            .WithName("GetJwks")
            .WithTags("Keys")
            .WithSummary("Public keys used to verify DLE signatures.")
            .WithDescription(
                "Lists every key still inside its validity window, so a key published before it "
                + "starts signing and a key kept after it stopped are both present. Verifiers "
                + "should select by the kid carried in the signature and refresh on an unknown one.")
            .Produces<JwksDocument>(StatusCodes.Status200OK, JwkSetContentType);

        return app;
    }

    /// <summary>Builds, caches and serves the key set.</summary>
    /// <param name="context">The request.</param>
    /// <param name="keyRing">The token signing keys (§E.4.2).</param>
    /// <param name="contributors">Modules with keys of their own, for example webhook signing.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <returns>The key set, or 304 when the caller already has this version.</returns>
    private static Results<ContentHttpResult, StatusCodeHttpResult> GetJwks(
        HttpContext context,
        IKeyRing keyRing,
        IEnumerable<IJwksContributor> contributors,
        TimeProvider timeProvider)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        // Ordinal comparison of key identifiers: a kid is an opaque token, and two kids that differ
        // only by case are two keys.
        Dictionary<string, JsonWebKey> keys = new(StringComparer.Ordinal);

        foreach (JsonWebKey key in keyRing.GetJwks().Keys)
        {
            keys.TryAdd(key.Kid, key);
        }

        foreach (IJwksContributor contributor in contributors)
        {
            foreach (JsonWebKey key in contributor.GetKeys(now))
            {
                if (IsInsideWindow(key, now))
                {
                    keys.TryAdd(key.Kid, key);
                }
            }
        }

        List<JsonWebKey> published = [.. keys.Values];
        published.Sort(static (left, right) => string.CompareOrdinal(left.Kid, right.Kid));

        // Source generated metadata rather than reflection, as the shared kernel requires of every
        // document the product emits (SHARED-KERNEL §0).
        string json = JsonSerializer.Serialize(
            new JwksDocument(published),
            DleDomainJsonContext.Default.JwksDocument);

        string etag = ComputeETag(json);

        context.Response.Headers.CacheControl =
            string.Create(CultureInfo.InvariantCulture, $"public, max-age={CacheSeconds}");
        context.Response.Headers.ETag = etag;

        if (MatchesETag(context.Request.Headers.IfNoneMatch, etag))
        {
            return TypedResults.StatusCode(StatusCodes.Status304NotModified);
        }

        return TypedResults.Content(json, JwkSetContentType);
    }

    /// <summary>Whether a contributed key may still be needed by a verifier.</summary>
    /// <param name="key">The key.</param>
    /// <param name="now">The current instant.</param>
    /// <returns><see langword="true"/> when the key has not passed its <c>not_after</c>.</returns>
    private static bool IsInsideWindow(JsonWebKey key, DateTimeOffset now) =>
        key.NotAfter is null || key.NotAfter.Value >= now;

    /// <summary>Derives a strong entity tag from the rendered document.</summary>
    /// <param name="json">The rendered document.</param>
    /// <returns>The quoted entity tag.</returns>
    /// <remarks>
    /// SHA-256 of the exact bytes served, so the tag changes when and only when the document does.
    /// It is not a secret and is not compared against one, so no constant time comparison applies
    /// here.
    /// </remarks>
    private static string ComputeETag(string json)
    {
        byte[] hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"\"{Convert.ToHexStringLower(hash.AsSpan(0, 16))}\"");
    }

    /// <summary>Whether the caller already holds this version.</summary>
    /// <param name="ifNoneMatch">The <c>If-None-Match</c> header values.</param>
    /// <param name="etag">The entity tag of the current document.</param>
    /// <returns><see langword="true"/> when one of the supplied tags matches.</returns>
    private static bool MatchesETag(IEnumerable<string?> ifNoneMatch, string etag)
    {
        foreach (string? candidate in ifNoneMatch)
        {
            if (candidate is null)
            {
                continue;
            }

            foreach (string part in candidate.Split(','))
            {
                string trimmed = part.Trim();

                if (string.Equals(trimmed, "*", StringComparison.Ordinal)
                    || string.Equals(trimmed, etag, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
