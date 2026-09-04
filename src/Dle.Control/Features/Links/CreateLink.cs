using Dle.Control.Features.Shared;
using Dle.Control.Identity;

namespace Dle.Control.Features.Links;

/// <summary>
/// <c>POST /api/v1/links</c> — creates one link (FR-101, FR-102, FR-104, FR-105).
/// </summary>
public static class CreateLink
{
    /// <summary>Creates a link.</summary>
    /// <param name="request">The link to create.</param>
    /// <param name="http">The request, for the authenticated caller.</param>
    /// <param name="writer">Validation, slug allocation and the write itself.</param>
    /// <param name="presenter">Builds the short URL and the QR URL.</param>
    /// <param name="audit">The administrative audit trail (FR-246).</param>
    /// <param name="cache">Drops any negative cache entry the edge holds for this slug.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>201 with the created link, or a problem document.</returns>
    /// <remarks>
    /// The cache invalidation matters even on a create, because the edge caches the absence of a
    /// slug as well as its presence: without it, a link created a second after somebody mistyped its
    /// URL would keep answering 404 for the length of the negative cache window (ADR-005).
    /// </remarks>
    public static async Task<IResult> HandleAsync(
        CreateLinkRequest request,
        HttpContext http,
        LinkWriteService writer,
        LinkPresentation presenter,
        AuditLogWriter audit,
        ILinkCacheInvalidator cache,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(presenter);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(cache);

        DleCaller caller = http.RequireDleCaller();

        LinkWriteOutcome outcome = await writer.CreateAsync(request, caller, cancellationToken);

        if (!outcome.Succeeded)
        {
            return LinkWriteProblem.ToResult(outcome);
        }

        Link link = outcome.Link!;
        string host = outcome.Host!;

        await AdministrativeAudit.RecordAsync(
            audit,
            caller,
            AuditActions.LinkCreated,
            AuditActions.LinkSubject,
            link.Id.ToString(CultureInfo.InvariantCulture),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["slug"] = link.Slug,
                ["host"] = host,
                ["domain_id"] = link.DomainId.ToString(),
            },
            cancellationToken);

        await cache.InvalidateLinkAsync(host, link.Slug, cancellationToken);

        LinkResponse response = presenter.Project(link, host);

        return TypedResults.Created(
            string.Create(CultureInfo.InvariantCulture, $"/api/v1/links/{response.Id}"),
            response);
    }
}
