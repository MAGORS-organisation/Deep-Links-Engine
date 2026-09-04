using System.Text.Json;

using Dle.Control.Features.Shared;
using Dle.Control.Identity;
using Dle.Control.Infrastructure;

using Microsoft.EntityFrameworkCore;

namespace Dle.Control.Features.Links;

/// <summary>
/// The stored form of a link template (FR-108).
/// </summary>
/// <remarks>
/// §B.5.2 gives <c>campaigns</c> one JSON column, named for the UTM map it was originally meant to
/// hold. A template is a campaign — that is what "campaign prefills the UTM parameters and the
/// rules" means — so the whole template document lives in that column rather than in a new table
/// nothing else references.
/// </remarks>
public sealed record LinkTemplateDocument
{
    /// <summary>UTM parameters copied into links created from this template.</summary>
    public IReadOnlyDictionary<string, string> Utm { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Routing rules copied into a link that supplies none of its own.</summary>
    public IReadOnlyList<RoutingRule> RoutingRules { get; init; } = [];

    /// <summary>Default web fallback target.</summary>
    public string? TargetUrl { get; init; }

    /// <summary>Default deep link path.</summary>
    public string? DeeplinkPath { get; init; }

    /// <summary>Default Open Graph metadata (FR-105).</summary>
    public OgMeta? Og { get; init; }

    /// <summary>Tags added to every link created from this template (FR-109).</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>
/// Reads and writes link templates, which are stored as campaigns (FR-108).
/// </summary>
/// <remarks>
/// The context is used directly rather than through a repository because there is no other consumer
/// of <c>campaigns</c> in the control plane, and a repository whose only caller is one feature is a
/// layer without a boundary. The tenant query filter applies exactly as it does everywhere else, so
/// a template belonging to another tenant is simply not in the queryable (TC-166).
/// </remarks>
public sealed class LinkTemplateStore
{
    /// <summary>Property name that distinguishes a template document from a bare UTM map.</summary>
    private const string TemplateMarker = "utm";

    private readonly DleDbContext _db;

    /// <summary>Creates the store.</summary>
    /// <param name="db">The control-plane context.</param>
    public LinkTemplateStore(DleDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <summary>Lists the tenant's templates, newest first.</summary>
    /// <param name="limit">Maximum number of rows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The templates.</returns>
    public async Task<IReadOnlyList<Campaign>> ListAsync(int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return await _db.Campaigns
            .AsNoTracking()
            .OrderByDescending(c => c.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Loads one template.</summary>
    /// <param name="id">The template identifier, which is also the campaign identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The template, or <see langword="null"/>.</returns>
    public async Task<Campaign?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await _db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    /// <summary>Creates a template.</summary>
    /// <param name="name">Template name.</param>
    /// <param name="document">The template document.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored campaign row.</returns>
    public async Task<Campaign> CreateAsync(
        string name,
        LinkTemplateDocument document,
        CancellationToken cancellationToken)
    {
        Campaign campaign = new()
        {
            Name = name,
            Utm = Render(document),
        };

        _db.Campaigns.Add(campaign);
        await _db.SaveChangesAsync(cancellationToken);

        return campaign;
    }

    /// <summary>Replaces a template.</summary>
    /// <param name="id">The template identifier.</param>
    /// <param name="name">The new name.</param>
    /// <param name="document">The new document.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored campaign row, or <see langword="null"/> when there is no such template.</returns>
    public async Task<Campaign?> UpdateAsync(
        Guid id,
        string name,
        LinkTemplateDocument document,
        CancellationToken cancellationToken)
    {
        Campaign? campaign = await _db.Campaigns.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (campaign is null)
        {
            return null;
        }

        campaign.Name = name;
        campaign.Utm = Render(document);

        await _db.SaveChangesAsync(cancellationToken);

        return campaign;
    }

    /// <summary>Removes a template.</summary>
    /// <param name="id">The template identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when a row was removed.</returns>
    public async Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken)
    {
        Campaign? campaign = await _db.Campaigns.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (campaign is null)
        {
            return false;
        }

        _db.Campaigns.Remove(campaign);
        await _db.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>Renders a template document for the campaign column.</summary>
    /// <param name="document">The document.</param>
    /// <returns>The JSON to store.</returns>
    public static string Render(LinkTemplateDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return JsonSerializer.Serialize(document, DleJson.Default);
    }

    /// <summary>
    /// Reads a stored campaign column as a template document.
    /// </summary>
    /// <param name="json">The stored document.</param>
    /// <returns>The template, never <see langword="null"/>.</returns>
    /// <remarks>
    /// A row written by an older version holds a flat string map of UTM parameters and nothing else.
    /// It is read as a template with those UTM defaults and no rules, rather than as a parse failure:
    /// the column changed shape, the data did not become wrong.
    /// </remarks>
    public static LinkTemplateDocument Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new LinkTemplateDocument();
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new LinkTemplateDocument();
            }

            if (!document.RootElement.TryGetProperty(TemplateMarker, out JsonElement utm)
                || utm.ValueKind != JsonValueKind.Object)
            {
                return new LinkTemplateDocument
                {
                    Utm = ControlJson.ReadStringMap(json),
                };
            }

            return JsonSerializer.Deserialize<LinkTemplateDocument>(json, DleJson.Default)
                ?? new LinkTemplateDocument();
        }
        catch (JsonException)
        {
            return new LinkTemplateDocument();
        }
    }
}

/// <summary>
/// <c>GET</c>, <c>POST</c>, <c>PATCH</c> and <c>DELETE /api/v1/links/templates</c> (FR-108).
/// </summary>
/// <remarks>
/// Nothing here is validated as strictly as a link, because a template is not served. The rules are
/// checked when a link is created from it, against the same validator every other link passes
/// through: a half-finished template is a legitimate intermediate state, a half-finished link is not
/// (TC-105).
/// </remarks>
public static class LinkTemplates
{
    /// <summary>Longest template name accepted.</summary>
    private const int MaxNameLength = 200;

    /// <summary>Most templates returned in one listing.</summary>
    private const int ListLimit = 200;

    /// <summary>Lists the tenant's templates.</summary>
    /// <param name="templates">Template storage.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The templates.</returns>
    public static async Task<IResult> ListAsync(
        LinkTemplateStore templates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(templates);

        IReadOnlyList<Campaign> rows = await templates.ListAsync(ListLimit, cancellationToken);
        List<LinkTemplateResponse> items = new(rows.Count);

        foreach (Campaign row in rows)
        {
            items.Add(Project(row));
        }

        return TypedResults.Ok(new PagedResponse<LinkTemplateResponse>
        {
            Items = items,
            Total = items.Count,
        });
    }

    /// <summary>Reads one template.</summary>
    /// <param name="id">The template identifier.</param>
    /// <param name="templates">Template storage.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the template, or 404.</returns>
    public static async Task<IResult> GetAsync(
        Guid id,
        LinkTemplateStore templates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(templates);

        Campaign? campaign = await templates.GetAsync(id, cancellationToken);

        return campaign is null
            ? DleProblem.NotFound("There is no template with that identifier.")
            : TypedResults.Ok(Project(campaign));
    }

    /// <summary>Creates a template.</summary>
    /// <param name="request">The template.</param>
    /// <param name="http">The request, for the authenticated caller.</param>
    /// <param name="templates">Template storage.</param>
    /// <param name="audit">The administrative audit trail (FR-246).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>201 with the created template, or a problem document.</returns>
    public static async Task<IResult> CreateAsync(
        LinkTemplateRequest request,
        HttpContext http,
        LinkTemplateStore templates,
        AuditLogWriter audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(audit);

        if (!TryReadName(request.Name, out string name, out IResult? failure))
        {
            return failure!;
        }

        DleCaller caller = http.RequireDleCaller();
        Campaign campaign = await templates.CreateAsync(name, ToDocument(request), cancellationToken);

        await AdministrativeAudit.RecordAsync(
            audit,
            caller,
            AuditActions.TemplateCreated,
            AuditActions.TemplateSubject,
            campaign.Id.ToString(),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = name },
            cancellationToken);

        return TypedResults.Created(
            string.Create(CultureInfo.InvariantCulture, $"/api/v1/links/templates/{campaign.Id}"),
            Project(campaign));
    }

    /// <summary>Replaces a template.</summary>
    /// <param name="id">The template identifier.</param>
    /// <param name="request">The new content.</param>
    /// <param name="http">The request, for the authenticated caller.</param>
    /// <param name="templates">Template storage.</param>
    /// <param name="audit">The administrative audit trail (FR-246).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the template, or 404.</returns>
    public static async Task<IResult> UpdateAsync(
        Guid id,
        LinkTemplateRequest request,
        HttpContext http,
        LinkTemplateStore templates,
        AuditLogWriter audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(audit);

        if (!TryReadName(request.Name, out string name, out IResult? failure))
        {
            return failure!;
        }

        DleCaller caller = http.RequireDleCaller();

        Campaign? campaign =
            await templates.UpdateAsync(id, name, ToDocument(request), cancellationToken);

        if (campaign is null)
        {
            return DleProblem.NotFound("There is no template with that identifier.");
        }

        await AdministrativeAudit.RecordAsync(
            audit,
            caller,
            AuditActions.TemplateUpdated,
            AuditActions.TemplateSubject,
            campaign.Id.ToString(),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = name },
            cancellationToken);

        return TypedResults.Ok(Project(campaign));
    }

    /// <summary>Removes a template.</summary>
    /// <param name="id">The template identifier.</param>
    /// <param name="http">The request, for the authenticated caller.</param>
    /// <param name="templates">Template storage.</param>
    /// <param name="audit">The administrative audit trail (FR-246).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>204, or 404.</returns>
    /// <remarks>
    /// Links already created from the template keep the values they were given. A template is a
    /// starting point, not a live inheritance: an edit to a campaign that silently rewrote every link
    /// created from it would make a link's stored definition untrustworthy.
    /// </remarks>
    public static async Task<IResult> DeleteAsync(
        Guid id,
        HttpContext http,
        LinkTemplateStore templates,
        AuditLogWriter audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(audit);

        DleCaller caller = http.RequireDleCaller();

        if (!await templates.RemoveAsync(id, cancellationToken))
        {
            return DleProblem.NotFound("There is no template with that identifier.");
        }

        await AdministrativeAudit.RecordAsync(
            audit,
            caller,
            AuditActions.TemplateDeleted,
            AuditActions.TemplateSubject,
            id.ToString(),
            metadata: null,
            cancellationToken);

        return TypedResults.NoContent();
    }

    /// <summary>Projects a campaign row as a template.</summary>
    /// <param name="campaign">The stored row.</param>
    /// <returns>The representation.</returns>
    private static LinkTemplateResponse Project(Campaign campaign)
    {
        LinkTemplateDocument document = LinkTemplateStore.Parse(campaign.Utm);

        return new LinkTemplateResponse
        {
            Id = campaign.Id,
            Name = campaign.Name,
            Utm = document.Utm,
            RoutingRules = document.RoutingRules,
            TargetUrl = document.TargetUrl,
            DeeplinkPath = document.DeeplinkPath,
            Og = document.Og,
            Tags = document.Tags,
            CreatedAt = campaign.CreatedAt,
        };
    }

    /// <summary>Converts the request into the stored document.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The document.</returns>
    private static LinkTemplateDocument ToDocument(LinkTemplateRequest request) => new()
    {
        Utm = request.Utm,
        RoutingRules = request.RoutingRules,
        TargetUrl = request.TargetUrl,
        DeeplinkPath = request.DeeplinkPath,
        Og = request.Og,
        Tags = request.Tags,
    };

    /// <summary>Validates the template name.</summary>
    /// <param name="value">The supplied name.</param>
    /// <param name="name">The trimmed name.</param>
    /// <param name="failure">The problem document when the name is unusable.</param>
    /// <returns><see langword="true"/> when the name may be stored.</returns>
    private static bool TryReadName(string? value, out string name, out IResult? failure)
    {
        name = value?.Trim() ?? string.Empty;

        if (name.Length == 0 || name.Length > MaxNameLength)
        {
            failure = DleProblem.Validation(
                "name",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A template name is required and may be at most {MaxNameLength} characters."));

            return false;
        }

        failure = null;
        return true;
    }
}
