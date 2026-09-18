using Dle.Control.Features.Shared;
using Dle.Control.Identity;
using Dle.Control.Infrastructure;

using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace Dle.Control.Features.Links;

/// <summary>
/// Why a link could not be written. Rendered either as a problem document or as one line of a bulk
/// result, which is why it is a value rather than an <see cref="IResult"/>.
/// </summary>
public enum LinkWriteError
{
    /// <summary>The write succeeded.</summary>
    None = 0,

    /// <summary>A field failed validation.</summary>
    Validation = 1,

    /// <summary>The slug is syntactically invalid or reserved.</summary>
    SlugInvalid = 2,

    /// <summary>The slug is already taken on that domain.</summary>
    SlugTaken = 3,

    /// <summary>The target failed the safety policy (T-01, T-02, TC-161 to TC-163).</summary>
    UnsafeTarget = 4,

    /// <summary>The supplied rule set has no default rule (TC-105).</summary>
    MissingDefaultRule = 5,

    /// <summary>The supplied rule set is otherwise invalid.</summary>
    InvalidRoutingRules = 6,

    /// <summary>
    /// The domain does not exist, or belongs to another tenant. The two are deliberately the same
    /// answer (TC-166, SHARED-KERNEL §17.7).
    /// </summary>
    DomainNotFound = 7,

    /// <summary>The link does not exist, or belongs to another tenant.</summary>
    LinkNotFound = 8,

    /// <summary>
    /// The link changed between the caller's read and this write - another edit, or an abuse
    /// quarantine - and the write was refused rather than allowed to reverse it.
    /// </summary>
    Conflict = 9,

    /// <summary>
    /// The resolve-time fields are together larger than the covering index can hold (§B.5.2).
    /// </summary>
    TooLarge = 10,
}

/// <summary>
/// The outcome of writing a link.
/// </summary>
/// <param name="Link">The stored link, when the write succeeded.</param>
/// <param name="Host">Host of the serving domain, when the write succeeded.</param>
/// <param name="Error">Why it failed, or <see cref="LinkWriteError.None"/>.</param>
/// <param name="Detail">Human readable explanation of the failure.</param>
/// <param name="FieldErrors">Per field messages, for the <c>errors</c> extension of the problem document.</param>
public sealed record LinkWriteOutcome(
    Link? Link,
    string? Host,
    LinkWriteError Error,
    string? Detail,
    IReadOnlyDictionary<string, string[]>? FieldErrors)
{
    /// <summary>Whether the write succeeded.</summary>
    public bool Succeeded => Error == LinkWriteError.None && Link is not null;

    /// <summary>Builds a successful outcome.</summary>
    /// <param name="link">The stored link.</param>
    /// <param name="host">Host of the serving domain.</param>
    /// <returns>The outcome.</returns>
    public static LinkWriteOutcome Success(Link link, string host) =>
        new(link, host, LinkWriteError.None, null, null);

    /// <summary>Builds a failed outcome.</summary>
    /// <param name="error">Why it failed.</param>
    /// <param name="detail">Human readable explanation.</param>
    /// <param name="fieldErrors">Per field messages.</param>
    /// <returns>The outcome.</returns>
    public static LinkWriteOutcome Failure(
        LinkWriteError error,
        string detail,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null) =>
        new(null, null, error, detail, fieldErrors);
}

/// <summary>
/// Creates and edits links: validation, slug allocation and the write itself (FR-101, FR-102).
/// </summary>
/// <remarks>
/// <para>
/// Both the single-link endpoints and the streamed bulk import go through this class, which is why
/// it returns a value rather than a result. A rule that only the single-link path enforced would be
/// a rule the bulk path could be used to bypass, and a ten thousand row import is exactly how
/// somebody would try (FR-103, §E.3).
/// </para>
/// <para>
/// Nothing here ever takes a redirect target from anywhere but the request body of an authenticated
/// operator, and every such target is put through <see cref="TargetUrlPolicy"/> and
/// <see cref="IUrlSafetyChecker"/> before it is stored. That is the whole of prohibition 4: by the
/// time the edge reads a target, an operator has stored it and the safety pipeline has passed it
/// (SHARED-KERNEL §17.4, T-01, T-02).
/// </para>
/// </remarks>
public sealed class LinkWriteService
{
    /// <summary>PostgreSQL SQLSTATE for a unique violation.</summary>
    private const string UniqueViolation = "23505";

    /// <summary>Identifier given to the rule synthesised for a link that declares none.</summary>
    private const string SynthesisedDefaultRuleId = "default";

    /// <summary>Longest tag accepted, so the GIN index stays usable (FR-109).</summary>
    private const int MaxTagLength = 64;

    /// <summary>Most tags one link may carry.</summary>
    private const int MaxTags = 32;

    private readonly LinkRepository _links;
    private readonly DomainRepository _domains;
    private readonly SlugSequenceAllocator _sequence;
    private readonly ISlugGenerator _slugs;
    private readonly IUrlSafetyChecker _safety;
    private readonly SnowflakeIdGenerator _ids;
    private readonly LinkTemplateStore _templates;

    /// <summary>Creates the service.</summary>
    /// <param name="links">Link storage.</param>
    /// <param name="domains">Domain storage, used to resolve the serving host.</param>
    /// <param name="sequence">The slug counter (ADR-007).</param>
    /// <param name="slugs">The keyed permutation that turns a counter value into a slug.</param>
    /// <param name="safety">The target URL safety pipeline (§E.3).</param>
    /// <param name="ids">Mints the time-ordered link identifier before the insert.</param>
    /// <param name="templates">Campaign templates, which prefill a new link (FR-108).</param>
    public LinkWriteService(
        LinkRepository links,
        DomainRepository domains,
        SlugSequenceAllocator sequence,
        ISlugGenerator slugs,
        IUrlSafetyChecker safety,
        SnowflakeIdGenerator ids,
        LinkTemplateStore templates)
    {
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(domains);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(slugs);
        ArgumentNullException.ThrowIfNull(safety);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(templates);

        _links = links;
        _domains = domains;
        _sequence = sequence;
        _slugs = slugs;
        _safety = safety;
        _ids = ids;
        _templates = templates;
    }

    /// <summary>Creates one link (FR-101).</summary>
    /// <param name="request">The link to create.</param>
    /// <param name="caller">The authenticated operator, recorded on the first revision.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored link, or why it could not be stored.</returns>
    public async Task<LinkWriteOutcome> CreateAsync(
        CreateLinkRequest request,
        DleCaller caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        LinkDomain? domain = await _domains.GetAsync(request.DomainId, cancellationToken);

        if (domain is null)
        {
            return LinkWriteOutcome.Failure(
                LinkWriteError.DomainNotFound,
                "There is no such domain. Register the host first, or use one of the tenant's own.");
        }

        // A campaign is a template: it prefills what the request left out, and never overrides what
        // the request stated. That direction is the whole contract of FR-108 — the template is a
        // starting point, not a live inheritance that could rewrite a link from a distance.
        LinkTemplateDocument? template = null;

        if (request.CampaignId is Guid campaignId)
        {
            Campaign? campaign = await _templates.GetAsync(campaignId, cancellationToken);

            if (campaign is null)
            {
                return LinkWriteOutcome.Failure(
                    LinkWriteError.Validation,
                    "There is no such campaign.",
                    new Dictionary<string, string[]>(StringComparer.Ordinal)
                    {
                        ["campaign_id"] = ["The campaign does not exist."],
                    });
            }

            template = LinkTemplateStore.Parse(campaign.Utm);
        }

        string targetUrl = string.IsNullOrWhiteSpace(request.TargetUrl)
            ? template?.TargetUrl?.Trim() ?? string.Empty
            : request.TargetUrl.Trim();

        string? deeplinkPath = Trim(request.DeeplinkPath) ?? Trim(template?.DeeplinkPath);
        IReadOnlyList<string> tags = MergeTags(request.Tags, template?.Tags);

        Dictionary<string, string[]> fieldErrors = new(StringComparer.Ordinal);

        ValidateTags(tags, fieldErrors);
        ValidateWindow(request.StartsAt, request.ExpiresAt, fieldErrors);
        ValidateDeeplinkPath(deeplinkPath, "deeplink_path", fieldErrors);

        if (fieldErrors.Count > 0)
        {
            return LinkWriteOutcome.Failure(
                LinkWriteError.Validation,
                "One or more fields are unusable.",
                fieldErrors);
        }

        IReadOnlyList<RoutingRule>? supplied = request.RoutingRules is { Count: > 0 }
            ? request.RoutingRules
            : template?.RoutingRules is { Count: > 0 } fromTemplate ? fromTemplate : null;

        LinkWriteOutcome? syntaxFailure = ValidateTargetSyntax(targetUrl);

        if (syntaxFailure is not null)
        {
            return syntaxFailure;
        }

        IReadOnlyList<RoutingRule> rules = ResolveRules(supplied, targetUrl);

        LinkWriteOutcome? ruleFailure = ValidateRules(supplied, rules);

        if (ruleFailure is not null)
        {
            return ruleFailure;
        }

        LinkWriteOutcome? targetFailure =
            await ValidateTargetsAsync(targetUrl, request.ExpiredUrl, rules, cancellationToken);

        if (targetFailure is not null)
        {
            return targetFailure;
        }

        (string? slug, LinkWriteOutcome? slugFailure) =
            await ResolveSlugAsync(request.Slug, request.DomainId, cancellationToken);

        if (slug is null)
        {
            return slugFailure ?? LinkWriteOutcome.Failure(
                LinkWriteError.SlugInvalid,
                "The slug could not be settled.");
        }

        Link link = new()
        {
            Id = _ids.Next(),
            TenantId = caller.TenantId,
            DomainId = request.DomainId,
            Slug = slug,
            Title = Trim(request.Title),
            Description = Trim(request.Description),
            TargetUrl = targetUrl,
            DeeplinkPath = deeplinkPath,
            RoutingRules = ControlJson.WriteRoutingRules(rules),
            OgMeta = ControlJson.WriteOgMeta(request.Og ?? template?.Og),
            Utm = ControlJson.WriteStringMap(MergeUtm(request.Utm, template?.Utm)),
            CampaignId = request.CampaignId,
            Tags = NormalizeTags(tags),
            IsActive = request.IsActive,
            StartsAt = request.StartsAt,
            ExpiresAt = request.ExpiresAt,
            ExpiredUrl = Trim(request.ExpiredUrl),
            CreatedBy = caller.ActorId,
        };

        try
        {
            await _links.AddAsync(link, caller.ActorId, cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // Two callers raced for the same custom slug. The unique index is the arbiter; the loser
            // gets the same answer it would have got from the pre-check a moment earlier.
            return LinkWriteOutcome.Failure(
                LinkWriteError.SlugTaken,
                "That slug is already in use on this domain.");
        }
        catch (DbUpdateException exception) when (IsIndexRowTooLarge(exception))
        {
            return LinkWriteOutcome.Failure(LinkWriteError.TooLarge, TooLargeDetail);
        }

        return LinkWriteOutcome.Success(link, domain.Host);
    }

    /// <summary>Applies a partial change to a link and appends its revision (FR-107).</summary>
    /// <param name="id">The link identifier.</param>
    /// <param name="request">The fields to change.</param>
    /// <param name="caller">The authenticated operator.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored link, or why it could not be stored.</returns>
    /// <remarks>
    /// A link that belongs to another tenant is simply not in the queryable, so it arrives here as
    /// <see cref="LinkWriteError.LinkNotFound"/> — the same answer as an identifier nobody ever
    /// issued (TC-166).
    /// </remarks>
    public async Task<LinkWriteOutcome> UpdateAsync(
        long id,
        UpdateLinkRequest request,
        DleCaller caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        Link? link = await _links.GetAsync(id, includeQuarantined: false, cancellationToken);

        if (link is null)
        {
            return LinkWriteOutcome.Failure(
                LinkWriteError.LinkNotFound,
                "There is no link with that identifier.");
        }

        LinkDomain? domain = await _domains.GetAsync(link.DomainId, cancellationToken);

        if (domain is null)
        {
            return LinkWriteOutcome.Failure(
                LinkWriteError.DomainNotFound,
                "The domain serving this link no longer exists.");
        }

        Dictionary<string, string[]> fieldErrors = new(StringComparer.Ordinal);

        if (request.Tags is not null)
        {
            ValidateTags(request.Tags, fieldErrors);
        }

        ValidateWindow(
            request.StartsAt ?? link.StartsAt,
            request.ExpiresAt ?? link.ExpiresAt,
            fieldErrors);

        if (request.DeeplinkPath is not null)
        {
            ValidateDeeplinkPath(request.DeeplinkPath, "deeplink_path", fieldErrors);
        }

        if (fieldErrors.Count > 0)
        {
            return LinkWriteOutcome.Failure(
                LinkWriteError.Validation,
                "One or more fields are unusable.",
                fieldErrors);
        }

        string targetUrl = request.TargetUrl?.Trim() ?? link.TargetUrl;

        if (request.TargetUrl is not null)
        {
            LinkWriteOutcome? syntaxFailure = ValidateTargetSyntax(targetUrl);

            if (syntaxFailure is not null)
            {
                return syntaxFailure;
            }
        }

        IReadOnlyList<RoutingRule> rules = request.RoutingRules is null
            ? Retarget(ControlJson.ReadRoutingRules(link.RoutingRules), link.TargetUrl, request.TargetUrl)
            : ResolveRules(request.RoutingRules, targetUrl);

        if (request.RoutingRules is not null)
        {
            LinkWriteOutcome? ruleFailure = ValidateRules(request.RoutingRules, rules);

            if (ruleFailure is not null)
            {
                return ruleFailure;
            }
        }

        // The target is re-validated on every change, not only on creation. A link whose target was
        // safe in March and is a phishing page in June is the ordinary case, and an edit is the one
        // moment the engine gets to look again (§E.3).
        bool targetChanged = request.TargetUrl is not null;
        bool expiredChanged = request.ExpiredUrl is not null;

        if (targetChanged || expiredChanged || request.RoutingRules is not null)
        {
            LinkWriteOutcome? targetFailure = await ValidateTargetsAsync(
                targetUrl,
                request.ExpiredUrl ?? link.ExpiredUrl,
                rules,
                cancellationToken);

            if (targetFailure is not null)
            {
                return targetFailure;
            }
        }

        link.Title = request.Title is null ? link.Title : Trim(request.Title);
        link.Description = request.Description is null ? link.Description : Trim(request.Description);
        link.TargetUrl = targetUrl;
        link.DeeplinkPath = request.DeeplinkPath is null ? link.DeeplinkPath : Trim(request.DeeplinkPath);
        link.RoutingRules = ControlJson.WriteRoutingRules(rules);
        link.OgMeta = request.Og is null ? link.OgMeta : ControlJson.WriteOgMeta(request.Og);
        link.Utm = request.Utm is null ? link.Utm : ControlJson.WriteStringMap(request.Utm);
        link.CampaignId = request.CampaignId ?? link.CampaignId;
        link.Tags = request.Tags is null ? link.Tags : NormalizeTags(request.Tags);
        link.IsActive = request.IsActive ?? link.IsActive;
        link.StartsAt = request.StartsAt ?? link.StartsAt;
        link.ExpiresAt = request.ExpiresAt ?? link.ExpiresAt;
        link.ExpiredUrl = request.ExpiredUrl is null ? link.ExpiredUrl : Trim(request.ExpiredUrl);

        try
        {
            await _links.UpdateAsync(link, caller.ActorId, request.ChangeNote, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The version read at the top of this method is no longer the row's version: another
            // edit, or an abuse quarantine, landed in between. Overwriting it would silently undo
            // that write, so the caller is told to read again (T-09, TC-103).
            return LinkWriteOutcome.Failure(
                LinkWriteError.Conflict,
                "The link changed while this request was being prepared. Read it again and retry.");
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return LinkWriteOutcome.Failure(
                LinkWriteError.SlugTaken,
                "That slug is already in use on this domain.");
        }
        catch (DbUpdateException exception) when (IsIndexRowTooLarge(exception))
        {
            return LinkWriteOutcome.Failure(LinkWriteError.TooLarge, TooLargeDetail);
        }

        return LinkWriteOutcome.Success(link, domain.Host);
    }

    /// <summary>
    /// Chooses the rule set that will be stored.
    /// </summary>
    /// <param name="supplied">The rules the caller sent, possibly empty.</param>
    /// <param name="targetUrl">The web fallback target.</param>
    /// <returns>The rules to store.</returns>
    /// <remarks>
    /// A caller that sends no rules at all is describing the ordinary short link: one target, no
    /// conditions. Rather than making every such caller write out a default rule, one is synthesised
    /// from the target URL — which is what the link means anyway, and which keeps the invariant the
    /// edge relies on: a stored link always has exactly one default rule. A caller that <em>does</em>
    /// send a rule set is held to it, and a set without a default rule is refused (TC-105).
    /// </remarks>
    private static IReadOnlyList<RoutingRule> ResolveRules(
        IReadOnlyList<RoutingRule>? supplied,
        string targetUrl)
    {
        if (supplied is { Count: > 0 })
        {
            return supplied;
        }

        return
        [
            new RoutingRule
            {
                Id = SynthesisedDefaultRuleId,
                When = null,
                Then = new RuleAction
                {
                    Action = RoutingActionKind.Web,
                    Url = targetUrl?.Trim(),
                },
            },
        ];
    }

    /// <summary>Validates the rule set that will be stored.</summary>
    /// <param name="supplied">What the caller sent, so an omitted set is not blamed on them.</param>
    /// <param name="effective">The set that would be stored.</param>
    /// <returns>A failure, or <see langword="null"/> when the set may be stored.</returns>
    private static LinkWriteOutcome? ValidateRules(
        IReadOnlyList<RoutingRule>? supplied,
        IReadOnlyList<RoutingRule> effective)
    {
        IReadOnlyList<RoutingValidationError> errors = RoutingRuleValidator.Validate(effective);

        if (errors.Count == 0)
        {
            return null;
        }

        bool hasDefault = false;

        foreach (RoutingRule rule in effective)
        {
            if (rule?.When is null)
            {
                hasDefault = true;
                break;
            }
        }

        Dictionary<string, string[]> fieldErrors = new(StringComparer.Ordinal);

        foreach (RoutingValidationError error in errors)
        {
            string path = string.Create(CultureInfo.InvariantCulture, $"routing_{error.Path}");

            fieldErrors[path] = fieldErrors.TryGetValue(path, out string[]? existing)
                ? [.. existing, error.Message]
                : [error.Message];
        }

        if (!hasDefault && supplied is { Count: > 0 })
        {
            // TC-105: this is the rejection that keeps a link from being stored in a state where a
            // client matching nothing silently receives 404 for a link the marketer believes is live.
            return LinkWriteOutcome.Failure(
                LinkWriteError.MissingDefaultRule,
                "The rule set has no default rule. Exactly one rule must omit \"when\", and it must "
                + "be last, so that every client resolves to a target.",
                fieldErrors);
        }

        return LinkWriteOutcome.Failure(
            LinkWriteError.InvalidRoutingRules,
            "The routing rules are invalid.",
            fieldErrors);
    }

    /// <summary>
    /// Moves a catch-all web rule that was pointing at the link's target along with the target, so
    /// that a <c>PATCH</c> of <c>target_url</c> alone cannot leave the two disagreeing.
    /// </summary>
    /// <param name="stored">The rule set as it is stored.</param>
    /// <param name="previousTarget">The target the stored rules were written against.</param>
    /// <param name="requestedTarget">The new target, or <see langword="null"/> when the request leaves it alone.</param>
    /// <returns>The rule set to store.</returns>
    /// <remarks>
    /// A link created with only a <c>target_url</c> is stored with a default rule synthesised from
    /// that URL, and the edge routes from the rules alone: <c>target_url</c> is never read at
    /// resolve time. Writing the stored rules back unchanged after a target change therefore kept
    /// every visitor going to the old destination while the API, the revision history and the
    /// console all reported the new one — a link silently serving the wrong page, with nothing in
    /// the product disagreeing with anything else.
    /// </remarks>
    /// <remarks>
    /// Only a rule that was in step with the target moves: a catch-all (no <c>when</c>) web rule
    /// whose URL is the previous target. A rule set the caller authored to send the default case
    /// somewhere other than <c>target_url</c> is left exactly as written, because there the two
    /// fields are meant to differ.
    /// </remarks>
    private static IReadOnlyList<RoutingRule> Retarget(
        IReadOnlyList<RoutingRule> stored,
        string? previousTarget,
        string? requestedTarget)
    {
        string? target = Trim(requestedTarget);

        if (target is null || string.IsNullOrWhiteSpace(previousTarget))
        {
            return stored;
        }

        string previous = previousTarget.Trim();
        List<RoutingRule>? moved = null;

        for (int i = 0; i < stored.Count; i++)
        {
            RoutingRule rule = stored[i];

            if (rule.When is not null
                || rule.Then.Action != RoutingActionKind.Web
                || !string.Equals(rule.Then.Url?.Trim(), previous, StringComparison.Ordinal))
            {
                continue;
            }

            moved ??= [.. stored];
            moved[i] = rule with { Then = rule.Then with { Url = target } };
        }

        return moved ?? stored;
    }

    /// <summary>
    /// Refuses a target the safety policy would refuse on sight — a <c>javascript:</c>, <c>data:</c>
    /// or relative URL, a forbidden host name — before the rule set is derived from it (§E.3 step 1,
    /// TC-161).
    /// </summary>
    /// <param name="targetUrl">The web fallback target, or nothing when the rules supply every target.</param>
    /// <returns>A failure, or <see langword="null"/> when the target may go on to the rules and the full check.</returns>
    /// <remarks>
    /// Without this the default rule synthesised from the target carries the bad URL, and a caller
    /// who sent only <c>target_url</c> is told about <c>routing_rules[0].then.url</c>, a field that
    /// was never in the request, under the wrong problem type. The full check runs afterwards as
    /// before; this is the same syntax verdict, delivered against the right field.
    /// </remarks>
    private static LinkWriteOutcome? ValidateTargetSyntax(string? targetUrl)
    {
        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            return null;
        }

        UrlSafetyVerdict verdict = TargetUrlPolicy.ValidateSyntax(targetUrl);

        if (verdict.Level == UrlSafetyLevel.Safe)
        {
            return null;
        }

        return LinkWriteOutcome.Failure(
            LinkWriteError.UnsafeTarget,
            string.Create(
                CultureInfo.InvariantCulture,
                $"The target was refused by the {verdict.Source} check: {verdict.Reason}"),
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["target_url"] =
                [
                    verdict.Reason
                        ?? "The target is not a permitted redirect destination.",
                ],
            });
    }

    /// <summary>
    /// Puts every redirect target through the safety policy (T-01, T-02, TC-161 to TC-163).
    /// </summary>
    /// <param name="targetUrl">The web fallback target.</param>
    /// <param name="expiredUrl">Where an expired link sends the visitor.</param>
    /// <param name="rules">The rule set, whose actions carry targets of their own.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A failure, or <see langword="null"/> when every target passed.</returns>
    /// <remarks>
    /// Every distinct URL is checked once. That matters for a bulk import: a ten thousand row batch
    /// pointing at one campaign landing page performs one reputation lookup per row otherwise, and
    /// the checker's own network work is the slowest part of a create.
    /// </remarks>
    private async Task<LinkWriteOutcome?> ValidateTargetsAsync(
        string? targetUrl,
        string? expiredUrl,
        IReadOnlyList<RoutingRule> rules,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> candidates = new(StringComparer.OrdinalIgnoreCase);

        Add(candidates, targetUrl, "target_url");
        Add(candidates, expiredUrl, "expired_url");

        for (int i = 0; i < rules.Count; i++)
        {
            RuleAction? action = rules[i]?.Then;

            if (action is null)
            {
                continue;
            }

            Add(candidates, action.Url, string.Create(CultureInfo.InvariantCulture, $"routing_rules[{i}].then.url"));
            Add(candidates, action.StoreUrl, string.Create(CultureInfo.InvariantCulture, $"routing_rules[{i}].then.store_url"));
        }

        if (candidates.Count == 0)
        {
            return LinkWriteOutcome.Failure(
                LinkWriteError.Validation,
                "A link needs a target.",
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["target_url"] = ["An absolute http or https target URL is required."],
                });
        }

        foreach (KeyValuePair<string, string> candidate in candidates)
        {
            UrlSafetyVerdict verdict = await _safety.CheckAsync(candidate.Key, cancellationToken);

            if (verdict.Level == UrlSafetyLevel.Safe)
            {
                continue;
            }

            return LinkWriteOutcome.Failure(
                LinkWriteError.UnsafeTarget,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The target was refused by the {verdict.Source} check: {verdict.Reason}"),
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    [candidate.Value] =
                    [
                        verdict.Reason
                            ?? "The target is not a permitted redirect destination.",
                    ],
                });
        }

        return null;

        static void Add(Dictionary<string, string> into, string? url, string field)
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                into.TryAdd(url.Trim(), field);
            }
        }
    }

    /// <summary>
    /// Settles the slug: the caller's, checked, or a fresh one from the counter (FR-102, ADR-007).
    /// </summary>
    /// <param name="requested">The slug the caller asked for, or <see langword="null"/>.</param>
    /// <param name="domainId">The serving domain, within which slugs are unique.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The settled slug, or the failure that prevents one being settled.</returns>
    /// <remarks>
    /// A generated slug needs no collision check at all: the counter value is unique and the
    /// permutation is a bijection, so the slug is unique by construction. Only a custom slug is
    /// looked up, and the unique index remains the final arbiter for the race between the check and
    /// the insert.
    /// </remarks>
    private async Task<(string? Slug, LinkWriteOutcome? Failure)> ResolveSlugAsync(
        string? requested,
        Guid domainId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            long sequence = await _sequence.NextAsync(cancellationToken);
            return (_slugs.FromSequence(sequence), null);
        }

        if (!SlugPolicy.TryNormalize(requested, out string slug))
        {
            return (null, LinkWriteOutcome.Failure(
                LinkWriteError.SlugInvalid,
                "The slug contains characters that are not allowed. Use letters, digits and hyphens."));
        }

        if (SlugPolicy.IsReserved(slug))
        {
            return (null, LinkWriteOutcome.Failure(
                LinkWriteError.SlugInvalid,
                "That slug is reserved by the engine and cannot be used for a link."));
        }

        if (!SlugPolicy.IsValidCustom(slug))
        {
            return (null, LinkWriteOutcome.Failure(
                LinkWriteError.SlugInvalid,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A custom slug must be {SlugPolicy.MinCustomLength} to "
                    + $"{SlugPolicy.GeneratedLength - 1} characters, or at least "
                    + $"{SlugPolicy.GeneratedLength + 1}, so that it cannot be confused with a "
                    + $"generated one.")));
        }

        if (await _links.SlugExistsAsync(domainId, slug, cancellationToken))
        {
            return (null, LinkWriteOutcome.Failure(
                LinkWriteError.SlugTaken,
                "That slug is already in use on this domain."));
        }

        return (slug, null);
    }

    /// <summary>Checks the tag list (FR-109).</summary>
    /// <param name="tags">The tags supplied.</param>
    /// <param name="errors">Field errors collected so far.</param>
    private static void ValidateTags(IReadOnlyList<string> tags, Dictionary<string, string[]> errors)
    {
        if (tags.Count > MaxTags)
        {
            errors["tags"] =
            [
                string.Create(CultureInfo.InvariantCulture, $"A link may carry at most {MaxTags} tags."),
            ];

            return;
        }

        foreach (string tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag) || tag.Trim().Length > MaxTagLength)
            {
                errors["tags"] =
                [
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"A tag must be between 1 and {MaxTagLength} characters."),
                ];

                return;
            }
        }
    }

    /// <summary>Checks that the serving window makes sense (FR-104).</summary>
    /// <param name="startsAt">When the link starts serving.</param>
    /// <param name="expiresAt">When it stops.</param>
    /// <param name="errors">Field errors collected so far.</param>
    private static void ValidateWindow(
        DateTimeOffset? startsAt,
        DateTimeOffset? expiresAt,
        Dictionary<string, string[]> errors)
    {
        if (startsAt is DateTimeOffset from && expiresAt is DateTimeOffset to && to <= from)
        {
            errors["expires_at"] = ["The expiry must be later than the activation instant."];
        }
    }

    /// <summary>
    /// Checks that a deep link path is a path.
    /// </summary>
    /// <param name="deeplinkPath">The value supplied.</param>
    /// <param name="field">Field name for the error message.</param>
    /// <param name="errors">Field errors collected so far.</param>
    /// <remarks>
    /// Accepting an absolute or protocol-relative value here would turn the deep link path into an
    /// open redirect that never passes the target policy, which is the same hole
    /// <see cref="RoutingRuleValidator"/> closes inside a rule (T-01).
    /// </remarks>
    private static void ValidateDeeplinkPath(
        string? deeplinkPath,
        string field,
        Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrEmpty(deeplinkPath))
        {
            return;
        }

        bool looksAbsolute =
            deeplinkPath.Contains("://", StringComparison.Ordinal)
            || deeplinkPath.StartsWith("//", StringComparison.Ordinal)
            || deeplinkPath.StartsWith(@"\\", StringComparison.Ordinal);

        if (looksAbsolute)
        {
            errors[field] =
            [
                "The deep link path must be a path such as \"/product/123\", not an absolute or "
                + "protocol-relative URL.",
            ];
        }
    }

    /// <summary>Merges the template's UTM defaults under the link's own (FR-108).</summary>
    /// <param name="requested">What the request stated.</param>
    /// <param name="template">What the template supplies.</param>
    /// <returns>The merged map, with the request winning every collision.</returns>
    private static IReadOnlyDictionary<string, string> MergeUtm(
        IReadOnlyDictionary<string, string> requested,
        IReadOnlyDictionary<string, string>? template)
    {
        if (template is null || template.Count == 0)
        {
            return requested;
        }

        Dictionary<string, string> merged = new(template, StringComparer.Ordinal);

        foreach (KeyValuePair<string, string> entry in requested)
        {
            merged[entry.Key] = entry.Value;
        }

        return merged;
    }

    /// <summary>Combines the template's tags with the link's own (FR-108, FR-109).</summary>
    /// <param name="requested">What the request stated.</param>
    /// <param name="template">What the template supplies.</param>
    /// <returns>The union.</returns>
    private static IReadOnlyList<string> MergeTags(
        IReadOnlyList<string> requested,
        IReadOnlyList<string>? template)
    {
        if (template is null || template.Count == 0)
        {
            return requested;
        }

        List<string> merged = [.. requested];
        merged.AddRange(template);

        return merged;
    }

    /// <summary>Normalizes tags to lowercase, trimmed and distinct.</summary>
    /// <param name="tags">The tags supplied.</param>
    /// <returns>The tags to store.</returns>
    private static List<string> NormalizeTags(IReadOnlyList<string> tags)
    {
        List<string> normalized = new(tags.Count);

        foreach (string tag in tags)
        {
            string candidate = tag.Trim().ToLowerInvariant();

            if (candidate.Length > 0 && !normalized.Contains(candidate, StringComparer.Ordinal))
            {
                normalized.Add(candidate);
            }
        }

        return normalized;
    }

    /// <summary>Trims a value, turning an empty result into <see langword="null"/>.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The trimmed value, or <see langword="null"/>.</returns>
    private static string? Trim(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    /// <summary>Tells a unique violation from any other write failure.</summary>
    /// <param name="exception">The failure reported by EF Core.</param>
    /// <returns><see langword="true"/> when the failure is a unique violation.</returns>
    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgres
        && string.Equals(postgres.SqlState, UniqueViolation, StringComparison.Ordinal);

    /// <summary>
    /// SQLSTATE 54000 (program_limit_exceeded) is what PostgreSQL answers when a row's entry in
    /// the covering resolve index would exceed the btree maximum (§B.5.2). The fields that make
    /// up that entry are all caller supplied, so this is the caller's problem to shorten, not an
    /// outage and not a 500.
    /// </summary>
    private static bool IsIndexRowTooLarge(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgres
        && string.Equals(postgres.SqlState, ProgramLimitExceeded, StringComparison.Ordinal);

    private const string ProgramLimitExceeded = "54000";

    private const string TooLargeDetail =
        "The link's resolve-time fields (target URL, expired URL, title, UTM set, routing rules, "
        + "Open Graph metadata) are together too large for the resolve index. Shorten them; the "
        + "limit is roughly 2.7 kB after compression.";
}
