using Dle.Edge.Resolution;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Maps the resolve endpoint — the one route the whole edge exists for.
/// </summary>
/// <remarks>
/// The namespace is <c>Microsoft.AspNetCore.Builder</c>, matching every other module, so that the
/// composition root stays declarative.
/// </remarks>
public static class ResolveEndpointExtensions
{
    /// <summary>Route template of the resolve endpoint.</summary>
    /// <remarks>
    /// A single segment, and the constraint is on length only. Slug validity is decided by
    /// <see cref="SlugPolicy"/> inside the handler rather than by a route constraint, because a request
    /// that fails validation still has to be answered by the same code path with the same body and the
    /// same work — a route constraint would answer it earlier, more cheaply and therefore
    /// distinguishably.
    /// </remarks>
    public const string RoutePattern = "/{slug:minlength(1):maxlength(64)}";

    /// <summary>Route of the zero-JS deferred attribution consent submission form on interstitials.</summary>
    public const string ConsentRoutePattern = "/_dl/consent";

    /// <summary>
    /// Maps <c>GET /{slug}</c>, <c>HEAD /{slug}</c>, and <c>POST /_dl/consent</c>.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <c>HEAD</c> is mapped alongside <c>GET</c> because link checkers, chat clients and mail gateways
    /// use it constantly, and an unmapped <c>HEAD</c> answers 405 — which several of them treat as a
    /// broken link and refuse to render at all.
    /// </remarks>
    public static IEndpointRouteBuilder MapResolve(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapMethods(RoutePattern, [HttpMethods.Get, HttpMethods.Head], ResolveAsync)
            .WithName("Resolve")
            .WithTags("resolve")
            .WithSummary("Resolve a link")
            .WithDescription(
                "Classifies the client, evaluates the link's routing rules and answers with a 302 to the store or " +
                "the web target, a 200 interstitial for an in-app webview, a 200 Open Graph document for a " +
                "confirmed crawler, 410 for a withdrawn link, or 404. Never 301. " +
                "Adding ?_dl=preview evaluates everything and records no click event.")
            .Produces(StatusCodes.Status302Found)
            .Produces(StatusCodes.Status200OK, contentType: "text/html")
            .Produces(StatusCodes.Status404NotFound, contentType: "text/html")
            .Produces(StatusCodes.Status410Gone, contentType: "text/html")
            .Produces(StatusCodes.Status429TooManyRequests, contentType: "text/html")
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .AllowAnonymous();

        app.MapPost(ConsentRoutePattern, HandleConsentAsync)
            .WithName("ResolveConsent")
            .WithTags("resolve")
            .WithSummary("Record deferred attribution consent from an interstitial")
            .WithDescription(
                "Validates the single-use signed token emitted by an interstitial page on a full-consent " +
                "domain, writes the consented click event and optional iOS claim code, and re-renders the " +
                "interstitial with attributed store targets enabled.")
            .Produces(StatusCodes.Status200OK, contentType: "text/html")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound, contentType: "text/html")
            .Produces(StatusCodes.Status429TooManyRequests, contentType: "text/html")
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .AllowAnonymous();

        return app;
    }

    /// <summary>Handles one resolve request.</summary>
    private static Task<IResult> ResolveAsync(
        HttpContext context,
        string slug,
        LinkResolver resolver,
        CancellationToken cancellationToken) =>
        resolver.ResolveAsync(context, slug, cancellationToken);

    /// <summary>Handles a zero-JS deferred consent submission from an interstitial form.</summary>
    private static Task<IResult> HandleConsentAsync(
        HttpContext context,
        LinkResolver resolver,
        CancellationToken cancellationToken) =>
        resolver.HandleConsentAsync(context, cancellationToken);
}
