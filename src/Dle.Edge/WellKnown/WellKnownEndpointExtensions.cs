using Dle.Domain.Ports;
using Dle.Domain.Primitives;
using Dle.Domain.WellKnown;
using Dle.Edge.Telemetry;
using Dle.Edge.WellKnown;
using Dle.Persistence.Fast.Caching;

using Microsoft.Extensions.Caching.Hybrid;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Maps the two association files that make Universal Links and App Links work
/// (FR-141, FR-142, §C.3.3).
/// </summary>
/// <remarks>
/// <para>
/// Both endpoints are unforgiving and both fail silently. Apple fetches the file through its CDN and
/// caches whatever it gets; a device refreshes roughly once a week (§A.2.1). An empty document, a
/// redirect, or a content type carrying a charset parameter all produce the same symptom - the
/// application is simply never opened - and none of them produce an error anywhere. That is why the
/// handlers below answer 404 rather than an empty JSON body when a host has no application registered
/// (TC-122), and why the response is written byte for byte rather than through a helper.
/// </para>
/// <para>
/// <b>Composition requirement.</b> Nothing in the pipeline may redirect a request under
/// <see cref="WellKnownPaths.Prefix"/>. In practice that means <c>UseHttpsRedirection</c>,
/// <c>UseHsts</c> and any rewriter must skip that prefix, and TLS termination must serve it on the
/// same certificate as the links themselves (TC-124).
/// </para>
/// </remarks>
public static class WellKnownEndpointExtensions
{
    /// <summary>
    /// How long a generated document is held in <see cref="HybridCache"/>.
    /// </summary>
    /// <remarks>
    /// TC-125 requires a rule change to reach the file within fifteen minutes. The entries also carry
    /// the host tag, so <c>ILinkCacheInvalidator.InvalidateHostAsync</c> drops them immediately when the
    /// control plane edits a domain; the expiry is the backstop, not the mechanism.
    /// </remarks>
    public const int CacheMinutes = 15;

    private static readonly HybridCacheEntryOptions EntryOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(CacheMinutes),
        LocalCacheExpiration = TimeSpan.FromMinutes(CacheMinutes),
    };

    private const int MaxAgeSeconds = CacheMinutes * 60;

    /// <summary>
    /// Maps <c>GET /.well-known/apple-app-site-association</c> and
    /// <c>GET /.well-known/assetlinks.json</c>, both generated per host from
    /// <see cref="IDomainConfigStore"/>.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    public static IEndpointRouteBuilder MapWellKnown(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapMethods(WellKnownPaths.AppleAppSiteAssociation, [HttpMethods.Get, HttpMethods.Head], GetAasaAsync)
            .WithName("WellKnownAppleAppSiteAssociation")
            .WithTags("well-known")
            .WithSummary("Apple app site association")
            .WithDescription(
                "Generated per host from the applications registered on the link domain. " +
                "Served as application/json with no charset parameter and never redirected. " +
                "404 when the host has no iOS application: an empty document would be cached by Apple " +
                "for about a week.")
            .Produces(StatusCodes.Status200OK, contentType: WellKnownDocument.JsonContentType)
            .Produces(StatusCodes.Status304NotModified)
            .Produces(StatusCodes.Status404NotFound)
            .AllowAnonymous();

        app.MapMethods(WellKnownPaths.AssetLinks, [HttpMethods.Get, HttpMethods.Head], GetAssetLinksAsync)
            .WithName("WellKnownAssetLinks")
            .WithTags("well-known")
            .WithSummary("Android digital asset links")
            .WithDescription(
                "Generated per host, including the Android 15+ dynamic_app_link_components block. " +
                "Served as application/json with no charset parameter and never redirected. " +
                "404 when the host has no Android application.")
            .Produces(StatusCodes.Status200OK, contentType: WellKnownDocument.JsonContentType)
            .Produces(StatusCodes.Status304NotModified)
            .Produces(StatusCodes.Status404NotFound)
            .AllowAnonymous();

        return app;
    }

    /// <summary>Handles <c>GET /.well-known/apple-app-site-association</c>.</summary>
    private static async Task<IResult> GetAasaAsync(
        HttpContext context,
        IDomainConfigStore store,
        HybridCache cache,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!HostNormalizer.TryNormalize(context.Request.Host.Host, out string host))
        {
            return Results.NotFound();
        }

        try
        {
            WellKnownDocument? document = await cache.GetOrCreateAsync(
                LinkCacheKeys.Aasa(host),
                (Store: store, Host: host),
                static (state, token) => state.Store.BuildAasaAsync(state.Host, token),
                EntryOptions,
                tags: [LinkCacheKeys.HostTag(host)],
                cancellationToken: cancellationToken);

            return Respond(document);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            EdgeLog.WellKnownUnavailable(Logger(loggerFactory), AasaDocumentName, host, exception);
            return Unavailable(AasaDocumentName);
        }
    }

    /// <summary>Handles <c>GET /.well-known/assetlinks.json</c>.</summary>
    private static async Task<IResult> GetAssetLinksAsync(
        HttpContext context,
        IDomainConfigStore store,
        HybridCache cache,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!HostNormalizer.TryNormalize(context.Request.Host.Host, out string host))
        {
            return Results.NotFound();
        }

        try
        {
            WellKnownDocument? document = await cache.GetOrCreateAsync(
                LinkCacheKeys.AssetLinks(host),
                (Store: store, Host: host),
                static (state, token) => state.Store.BuildAssetLinksAsync(state.Host, token),
                EntryOptions,
                tags: [LinkCacheKeys.HostTag(host)],
                cancellationToken: cancellationToken);

            return Respond(document);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            EdgeLog.WellKnownUnavailable(Logger(loggerFactory), AssetLinksDocumentName, host, exception);
            return Unavailable(AssetLinksDocumentName);
        }
    }

    /// <summary>
    /// Turns a generated document into a response, or 404 when the host has nothing to publish.
    /// </summary>
    /// <remarks>
    /// The 404 is the whole point of the null case. Apple treats an empty or malformed document as a
    /// valid negative answer and caches it; a 404 is retried (TC-122). The same reasoning applies to
    /// Google, which re-verifies periodically on Android 15+.
    /// </remarks>
    private static IResult Respond(WellKnownDocument? document) =>
        document is null
            ? Results.NotFound()
            : new WellKnownJsonResult(document, MaxAgeSeconds);

    /// <summary>Name used for the Apple document in logs and problem documents.</summary>
    private const string AasaDocumentName = "apple-app-site-association";

    /// <summary>Name used for the Android document in logs and problem documents.</summary>
    private const string AssetLinksDocumentName = "assetlinks.json";

    /// <summary>
    /// Answers a request whose document could not be built because a dependency failed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 503, and deliberately neither of the two answers that look more natural here. A 500 would let
    /// the store's exception — connection strings, host names, stack frames — reach a public,
    /// unauthenticated endpoint, and would tell a load balancer the instance is broken rather than
    /// busy. A 404 would be worse still: 404 is this endpoint's way of saying "this host has no
    /// registered application", and answering it while the database is merely unreachable asserts
    /// something the process does not know. Apple and Google both retry a 503, so the association
    /// heals by itself once the dependency comes back.
    /// </para>
    /// <para>
    /// This mirrors the resolve path, which answers 503 rather than 500 for the same reason
    /// (SHARED-KERNEL §17.9: the branch that cannot decide refuses rather than guesses).
    /// </para>
    /// </remarks>
    private static IResult Unavailable(string document) =>
        Results.Problem(
            title: "The association document is temporarily unavailable.",
            detail: $"The {document} document for this host could not be built.",
            statusCode: StatusCodes.Status503ServiceUnavailable,
            type: ProblemCodes.DependencyUnavailable);

    /// <summary>
    /// The logger these handlers write through.
    /// </summary>
    /// <remarks>
    /// Taken from the factory rather than injected as <c>ILogger&lt;T&gt;</c> because the handlers are
    /// static methods of a static class, which has no closed generic to name.
    /// </remarks>
    private static ILogger Logger(ILoggerFactory factory) =>
        factory.CreateLogger("Dle.Edge.WellKnown");
}
