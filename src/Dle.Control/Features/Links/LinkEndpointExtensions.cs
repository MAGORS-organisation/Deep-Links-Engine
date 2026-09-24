using Dle.Control.Configuration;
using Dle.Control.Features.Links;
using Dle.Control.Features.Shared;
using Dle.Control.Identity;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Maps the link management routes (SHARED-KERNEL §15, §B.7.3).
/// </summary>
/// <remarks>
/// <para>
/// Every route carries an authorization policy and a rate limit policy by name. That is not
/// ceremony: the authorization fallback denies anything without one, and the rate limiter's global
/// limiter treats an unclassified API route as suspect, so forgetting either is a failure that shows
/// up immediately rather than a hole that shows up later (SHARED-KERNEL §17.9).
/// </para>
/// <para>
/// The literal <c>templates</c> segment is registered alongside <c>{id}</c>. ASP.NET Core's route
/// table prefers a literal segment over a parameter, so <c>/links/templates</c> can never be read as
/// a link whose identifier is the word "templates".
/// </para>
/// </remarks>
public static class LinkEndpointExtensions
{
    /// <summary>Maps <c>/api/v1/links</c> and everything beneath it.</summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    public static IEndpointRouteBuilder MapLinks(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder links = app.MapGroup("/api/v1/links").WithTags("Links");

        MapTemplateRoutes(links);
        MapCollectionRoutes(links);
        MapItemRoutes(links);

        return app;
    }

    /// <summary>Maps the routes that act on the collection as a whole.</summary>
    /// <param name="links">The link route group.</param>
    private static void MapCollectionRoutes(RouteGroupBuilder links)
    {
        links.MapGet("/", ListLinks.HandleAsync)
            .RequireAuthorization(DlePolicies.LinksRead)
            .RequireRateLimiting(DleRateLimitPolicies.Read)
            .WithName("ListLinks")
            .WithSummary("Lists links with search, tag filter and cursor paging.")
            .WithDescription(
                "Search matches the slug and the title case-insensitively. The tag filter is a "
                + "comma separated list and a link must carry every tag in it. Paging is by cursor: "
                + "pass the next_cursor of the previous page.")
            .Produces<PagedResponse<LinkResponse>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        links.MapPost("/", CreateLink.HandleAsync)
            .RequireAuthorization(DlePolicies.LinksWrite)
            .RequireRateLimiting(DleRateLimitPolicies.LinkCreate)
            .WithIdempotency()
            .WithName("CreateLink")
            .WithSummary("Creates a link.")
            .WithDescription(
                "The target is validated against the URL safety policy and the routing rules against "
                + "the rule validator; a rule set without a default rule is refused. Omit the slug to "
                + "have an eight character one generated. Send an Idempotency-Key to make a retry "
                + "replay the first response instead of creating a second link.")
            .Produces<LinkResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        links.MapPost("/bulk", BulkCreateLinks.HandleAsync)
            .RequireAuthorization(DlePolicies.LinksWrite)
            .RequireRateLimiting(DleRateLimitPolicies.Bulk)
            .WithName("BulkCreateLinks")
            .WithSummary("Creates a streamed batch of links from newline delimited JSON.")
            .WithDescription(
                "The request body is one BulkLinkItem per line and the response is one BulkLinkResult "
                + "per line, written as each row is processed. A failing row does not fail the batch. "
                + "At most two batches per tenant run at once and the row count is capped by "
                + "Dle:Control:BulkMaxRows, which defaults to ten thousand.")
            .Accepts<BulkLinkItem>(BulkCreateLinks.ContentType)
            .Produces<BulkLinkResult>(StatusCodes.Status200OK, BulkCreateLinks.ContentType)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }

    /// <summary>Maps the routes that act on one link.</summary>
    /// <param name="links">The link route group.</param>
    private static void MapItemRoutes(RouteGroupBuilder links)
    {
        links.MapGet("/{id}", ListLinks.GetAsync)
            .RequireAuthorization(DlePolicies.LinksRead)
            .RequireRateLimiting(DleRateLimitPolicies.Read)
            .WithName("GetLink")
            .WithSummary("Reads one link.")
            .WithDescription(
                "A link belonging to another tenant answers 404, exactly as an identifier nobody ever "
                + "issued does. The two are indistinguishable on purpose.")
            .Produces<LinkResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        links.MapPatch("/{id}", UpdateLink.HandleAsync)
            .RequireAuthorization(DlePolicies.LinksWrite)
            .RequireRateLimiting(DleRateLimitPolicies.Write)
            .WithIdempotency()
            .WithName("UpdateLink")
            .WithSummary("Changes a link and records the revision.")
            .WithDescription(
                "Only the supplied fields change. A replacement rule set is validated as a whole, "
                + "never merged, and a changed target is re-checked against the safety policy. Every "
                + "change appends a revision.")
            .Produces<LinkResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        links.MapPost("/{id}/archive", UpdateLink.ArchiveAsync)
            .RequireAuthorization(DlePolicies.LinksWrite)
            .RequireRateLimiting(DleRateLimitPolicies.Write)
            .WithIdempotency()
            .WithName("ArchiveLink")
            .WithSummary("Switches a link off without removing it.")
            .WithDescription(
                "The slug stays reserved and the history stays readable. A link withdrawn for abuse "
                + "is quarantined instead, which is a different operation with a different answer.")
            .Produces<LinkResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        links.MapDelete("/{id}", UpdateLink.DeleteAsync)
            .RequireAuthorization(DlePolicies.LinksWrite)
            .RequireRateLimiting(DleRateLimitPolicies.Write)
            .WithIdempotency()
            .WithName("DeleteLink")
            .WithSummary("Removes a link created by mistake.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        links.MapGet("/{id}/versions", ListLinks.GetVersionsAsync)
            .RequireAuthorization(DlePolicies.LinksRead)
            .RequireRateLimiting(DleRateLimitPolicies.Read)
            .WithName("ListLinkVersions")
            .WithSummary("Reads the revision history of a link.")
            .WithDescription(
                "Newest first. Each revision carries the whole link as it was, so the question after "
                + "an incident — what was this link serving at that moment — is answered without "
                + "replaying a chain of diffs.")
            .Produces<PagedResponse<LinkVersionSummary>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        links.MapGet(
                "/{id}/simulate",
                static (
                    string id,
                    [FromQuery(Name = "ua")] string? userAgent,
                    [FromQuery] string? platform,
                    [FromQuery] string? country,
                    [FromQuery] string? region,
                    [FromQuery] string? language,
                    [FromQuery(Name = "os_version")] string? osVersion,
                    [FromQuery(Name = "app_version")] string? appVersion,
                    [FromQuery] string? channel,
                    [FromQuery] DateTimeOffset? at,
                    [FromQuery(Name = "click_id")] string? clickId,
                    [FromHeader(Name = "Accept-Language")] string? acceptLanguage,
                    LinkRepository linkStore,
                    DomainRepository domainStore,
                    IRoutingEngine engine,
                    [FromServices] IClientClassifier? classifier,
                    TimeProvider timeProvider,
                    CancellationToken cancellationToken) => SimulateLink.HandleAsync(
                        id,
                        new SimulateRequest
                        {
                            UserAgent = userAgent,
                            Platform = platform,
                            Country = country,
                            Region = region,
                            Language = language,
                            OsVersion = osVersion,
                            AppVersion = appVersion,
                            Channel = channel,
                            At = at,
                            ClickId = clickId,
                        },
                        acceptLanguage,
                        linkStore,
                        domainStore,
                        engine,
                        classifier,
                        timeProvider,
                        cancellationToken))
            .RequireAuthorization(DlePolicies.LinksRead)
            .RequireRateLimiting(DleRateLimitPolicies.Read)
            .WithName("SimulateLink")
            .WithSummary("Answers what a given client would get, without generating a click.")
            .WithDescription(
                "Runs the same routing engine the edge runs and returns the decision together with a "
                + "line per rule saying why it matched or why it lost. Nothing is written: no click "
                + "event, no attribution. The explanation is available in English and Slovak through "
                + "Accept-Language.")
            .Produces<SimulateResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        links.MapPost(
                "/{id}/simulate",
                static (
                    string id,
                    SimulateRequest request,
                    [FromHeader(Name = "Accept-Language")] string? acceptLanguage,
                    LinkRepository linkStore,
                    DomainRepository domainStore,
                    IRoutingEngine engine,
                    [FromServices] IClientClassifier? classifier,
                    TimeProvider timeProvider,
                    CancellationToken cancellationToken) => SimulateLink.HandleAsync(
                        id,
                        request,
                        acceptLanguage,
                        linkStore,
                        domainStore,
                        engine,
                        classifier,
                        timeProvider,
                        cancellationToken))
            .RequireAuthorization(DlePolicies.LinksRead)
            .RequireRateLimiting(DleRateLimitPolicies.Read)
            .WithName("SimulateLinkFromBody")
            .WithSummary("The rule simulator, with the client described in the request body.")
            .Produces<SimulateResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    /// <summary>Maps the link template routes (FR-108).</summary>
    /// <param name="links">The link route group.</param>
    private static void MapTemplateRoutes(RouteGroupBuilder links)
    {
        RouteGroupBuilder templates = links.MapGroup("/templates").WithTags("Link templates");

        templates.MapGet("/", LinkTemplates.ListAsync)
            .RequireAuthorization(DlePolicies.LinksRead)
            .RequireRateLimiting(DleRateLimitPolicies.Read)
            .WithName("ListLinkTemplates")
            .WithSummary("Lists the campaign templates that prefill new links.")
            .Produces<PagedResponse<LinkTemplateResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        templates.MapGet("/{id:guid}", LinkTemplates.GetAsync)
            .RequireAuthorization(DlePolicies.LinksRead)
            .RequireRateLimiting(DleRateLimitPolicies.Read)
            .WithName("GetLinkTemplate")
            .WithSummary("Reads one template.")
            .Produces<LinkTemplateResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        templates.MapPost("/", LinkTemplates.CreateAsync)
            .RequireAuthorization(DlePolicies.LinksWrite)
            .RequireRateLimiting(DleRateLimitPolicies.Write)
            .WithIdempotency()
            .WithName("CreateLinkTemplate")
            .WithSummary("Creates a template.")
            .WithDescription(
                "A template is a campaign: creating one gives you a campaign identifier that a link "
                + "can name, and naming it prefills the UTM parameters, the rules and the target "
                + "defaults the link does not state for itself.")
            .Produces<LinkTemplateResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict);

        templates.MapPatch("/{id:guid}", LinkTemplates.UpdateAsync)
            .RequireAuthorization(DlePolicies.LinksWrite)
            .RequireRateLimiting(DleRateLimitPolicies.Write)
            .WithIdempotency()
            .WithName("UpdateLinkTemplate")
            .WithSummary("Replaces a template.")
            .WithDescription(
                "Links already created from the template keep the values they were given. A template "
                + "is a starting point, not a live inheritance.")
            .Produces<LinkTemplateResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        templates.MapDelete("/{id:guid}", LinkTemplates.DeleteAsync)
            .RequireAuthorization(DlePolicies.LinksWrite)
            .RequireRateLimiting(DleRateLimitPolicies.Write)
            .WithIdempotency()
            .WithName("DeleteLinkTemplate")
            .WithSummary("Removes a template.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
