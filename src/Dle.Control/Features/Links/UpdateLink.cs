using Dle.Control.Features.Shared;
using Dle.Control.Identity;
using Dle.Control.Infrastructure;

namespace Dle.Control.Features.Links;

/// <summary>
/// <c>PATCH /api/v1/links/{id}</c>, <c>POST /api/v1/links/{id}/archive</c> and
/// <c>DELETE /api/v1/links/{id}</c> (FR-101, FR-104, FR-107).
/// </summary>
/// <remarks>
/// Archiving and deleting are different operations on purpose. Archiving switches a link off and
/// keeps it, which is what a marketer means by "take the campaign down"; the slug stays reserved and
/// the history stays readable. Deleting removes the row, and is for a link created by mistake. A
/// link withdrawn for abuse is neither: it is quarantined by the abuse module, which keeps it
/// answering 410 with an explanation (§E.3, TC-103).
/// </remarks>
public static class UpdateLink
{
    /// <summary>Applies a partial change.</summary>
    /// <param name="id">The link identifier, as text because identifiers are 64 bit.</param>
    /// <param name="request">The fields to change.</param>
    /// <param name="http">The request, for the authenticated caller.</param>
    /// <param name="writer">Validation and the write itself.</param>
    /// <param name="presenter">Builds the short URL and the QR URL.</param>
    /// <param name="audit">The administrative audit trail (FR-246).</param>
    /// <param name="cache">Drops the edge's cached copy of the link.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the updated link, or a problem document.</returns>
    public static async Task<IResult> HandleAsync(
        string id,
        UpdateLinkRequest request,
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

        if (!LinkRoute.TryParseId(id, out long linkId))
        {
            return DleProblem.NotFound("There is no link with that identifier.");
        }

        DleCaller caller = http.RequireDleCaller();

        LinkWriteOutcome outcome = await writer.UpdateAsync(linkId, request, caller, cancellationToken);

        if (!outcome.Succeeded)
        {
            return LinkWriteProblem.ToResult(outcome);
        }

        Link link = outcome.Link!;
        string host = outcome.Host!;

        await AdministrativeAudit.RecordAsync(
            audit,
            caller,
            AuditActions.LinkUpdated,
            AuditActions.LinkSubject,
            link.Id.ToString(CultureInfo.InvariantCulture),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["slug"] = link.Slug,
                ["version"] = link.Version.ToString(CultureInfo.InvariantCulture),
                ["fields"] = ChangedFields(request),
            },
            cancellationToken);

        await cache.InvalidateLinkAsync(host, link.Slug, cancellationToken);

        return TypedResults.Ok(presenter.Project(link, host));
    }

    /// <summary>Switches a link off without removing it (FR-104).</summary>
    /// <param name="id">The link identifier.</param>
    /// <param name="http">The request, for the authenticated caller.</param>
    /// <param name="writer">Validation and the write itself.</param>
    /// <param name="presenter">Builds the short URL and the QR URL.</param>
    /// <param name="audit">The administrative audit trail (FR-246).</param>
    /// <param name="cache">Drops the edge's cached copy of the link.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the archived link, or a problem document.</returns>
    public static async Task<IResult> ArchiveAsync(
        string id,
        HttpContext http,
        LinkWriteService writer,
        LinkPresentation presenter,
        AuditLogWriter audit,
        ILinkCacheInvalidator cache,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(presenter);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(cache);

        if (!LinkRoute.TryParseId(id, out long linkId))
        {
            return DleProblem.NotFound("There is no link with that identifier.");
        }

        DleCaller caller = http.RequireDleCaller();

        UpdateLinkRequest request = new()
        {
            IsActive = false,
            ChangeNote = "Archived through the control plane.",
        };

        LinkWriteOutcome outcome = await writer.UpdateAsync(linkId, request, caller, cancellationToken);

        if (!outcome.Succeeded)
        {
            return LinkWriteProblem.ToResult(outcome);
        }

        Link link = outcome.Link!;
        string host = outcome.Host!;

        await AdministrativeAudit.RecordAsync(
            audit,
            caller,
            AuditActions.LinkArchived,
            AuditActions.LinkSubject,
            link.Id.ToString(CultureInfo.InvariantCulture),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["slug"] = link.Slug },
            cancellationToken);

        await cache.InvalidateLinkAsync(host, link.Slug, cancellationToken);

        return TypedResults.Ok(presenter.Project(link, host));
    }

    /// <summary>Removes a link created by mistake.</summary>
    /// <param name="id">The link identifier.</param>
    /// <param name="http">The request, for the authenticated caller.</param>
    /// <param name="links">Link storage.</param>
    /// <param name="domains">Domain storage, used to resolve the host for the cache drop.</param>
    /// <param name="audit">The administrative audit trail (FR-246).</param>
    /// <param name="cache">Drops the edge's cached copy of the link.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>204, or 404 when there is no such link.</returns>
    public static async Task<IResult> DeleteAsync(
        string id,
        HttpContext http,
        LinkRepository links,
        DomainRepository domains,
        AuditLogWriter audit,
        ILinkCacheInvalidator cache,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(domains);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(cache);

        if (!LinkRoute.TryParseId(id, out long linkId))
        {
            return DleProblem.NotFound("There is no link with that identifier.");
        }

        DleCaller caller = http.RequireDleCaller();

        Link? link = await links.GetAsync(linkId, includeQuarantined: false, cancellationToken);

        if (link is null)
        {
            return DleProblem.NotFound("There is no link with that identifier.");
        }

        LinkDomain? domain = await domains.GetAsync(link.DomainId, cancellationToken);

        if (!await links.RemoveAsync(linkId, cancellationToken))
        {
            return DleProblem.NotFound("There is no link with that identifier.");
        }

        await AdministrativeAudit.RecordAsync(
            audit,
            caller,
            AuditActions.LinkDeleted,
            AuditActions.LinkSubject,
            linkId.ToString(CultureInfo.InvariantCulture),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["slug"] = link.Slug },
            cancellationToken);

        if (domain is not null)
        {
            await cache.InvalidateLinkAsync(domain.Host, link.Slug, cancellationToken);
        }

        return TypedResults.NoContent();
    }

    /// <summary>
    /// Lists which fields the change touched, for the audit entry.
    /// </summary>
    /// <param name="request">The change.</param>
    /// <returns>A comma separated list of field names, never a value.</returns>
    /// <remarks>
    /// Names only. The audit trail records that the target URL changed, not what it changed to: the
    /// old value is in the link revision, which is where a forensic question belongs, and keeping the
    /// trail free of payload keeps it small enough to retain for years (FR-107, FR-246).
    /// </remarks>
    private static string ChangedFields(UpdateLinkRequest request)
    {
        List<string> fields = [];

        if (request.Title is not null)
        {
            fields.Add("title");
        }

        if (request.Description is not null)
        {
            fields.Add("description");
        }

        if (request.TargetUrl is not null)
        {
            fields.Add("target_url");
        }

        if (request.DeeplinkPath is not null)
        {
            fields.Add("deeplink_path");
        }

        if (request.RoutingRules is not null)
        {
            fields.Add("routing_rules");
        }

        if (request.Og is not null)
        {
            fields.Add("og");
        }

        if (request.Utm is not null)
        {
            fields.Add("utm");
        }

        if (request.CampaignId is not null)
        {
            fields.Add("campaign_id");
        }

        if (request.Tags is not null)
        {
            fields.Add("tags");
        }

        if (request.StartsAt is not null)
        {
            fields.Add("starts_at");
        }

        if (request.ExpiresAt is not null)
        {
            fields.Add("expires_at");
        }

        if (request.ExpiredUrl is not null)
        {
            fields.Add("expired_url");
        }

        if (request.IsActive is not null)
        {
            fields.Add("is_active");
        }

        return string.Join(',', fields);
    }
}

/// <summary>
/// Parses the identifier segment of a link route.
/// </summary>
/// <remarks>
/// A link identifier is a 64 bit Snowflake and travels as text, because JSON numbers above 2^53 are
/// not safe in every client. A value that is not a number is not an error: it is an identifier that
/// does not exist, and it gets the same 404 as one that does not exist (T-07).
/// </remarks>
internal static class LinkRoute
{
    /// <summary>Reads a link identifier from a route segment.</summary>
    /// <param name="value">The segment.</param>
    /// <param name="id">The identifier.</param>
    /// <returns><see langword="true"/> when the segment is a link identifier.</returns>
    internal static bool TryParseId(string? value, out long id) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
}
