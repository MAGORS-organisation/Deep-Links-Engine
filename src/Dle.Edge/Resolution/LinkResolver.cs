using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;

using Dle.Edge.Configuration;
using Dle.Edge.RateLimiting;
using Dle.Edge.Rendering;
using Dle.Edge.Telemetry;
using Dle.Persistence.Fast.Caching;
using Dle.Persistence.Fast.Configuration;

using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Dle.Edge.Resolution;

/// <summary>
/// The resolve pipeline of §B.6.1: normalize, look up, classify, gate on consent, evaluate the rules,
/// mint a click identifier, record the click without blocking, respond.
/// </summary>
/// <remarks>
/// <para>
/// The order is not decoration. Consent is evaluated <em>before</em> routing because it is an input to
/// the decision — it decides whether a click identifier may be attached to the outgoing URL at all —
/// and not a filter applied to a finished answer (§A principle 5, §E.6.2). The click identifier is
/// minted before the event is written because the event carries it, and the event is written before
/// the response is returned because the response must never wait on telemetry (FR-165).
/// </para>
/// <para>
/// The latency budget of §B.6.1 is 5.5 ms at the median against the 8 ms of NFR-01, which dictates the
/// I/O shape: exactly one cache lookup, falling through to exactly one indexed query on a miss, and
/// nothing else on the path that produces a redirect (SHARED-KERNEL §17.8). The three outcomes that
/// produce an HTML document take a second, separately cached read for the domain's branding; they are
/// already an order of magnitude more expensive to render and they are a small minority of traffic.
/// </para>
/// <para>
/// Every path that answers 404 does the same work in the same order — classify, gate, mint, record —
/// and returns the same bytes. That is the point of TC-102, TC-166 and T-07: a slug that never
/// existed, a slug whose link lives on another host and a slug whose link is not active are
/// indistinguishable to the client, so a scan learns nothing from either the body or the clock. On a
/// cache miss all of them also perform the same second cache write, so the expensive path is uniform
/// too. The single exception is a network prefix already in the enumeration shadow ban, answered
/// before any lookup at all — by then the client has proved it is scanning, and §E.9 asks for exactly
/// that.
/// </para>
/// </remarks>
public sealed class LinkResolver
{
    private readonly HybridCache _cache;
    private readonly ILinkStore _links;
    private readonly IDomainConfigStore _domains;
    private readonly IClientClassifier _classifier;
    private readonly IBotVerifier _botVerifier;
    private readonly IRoutingEngine _routing;
    private readonly IClickEventSink _sink;
    private readonly IClickIdCodec _clickIds;
    private readonly IIpHasher _ipHasher;
    private readonly NotFoundEnumerationGuard _guard;
    private readonly EdgeMetrics _metrics;
    private readonly EdgeCacheOptions _cacheOptions;
    private readonly EdgeOptions _options;
    private readonly EdgePrivacyOptions _privacy;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LinkResolver> _logger;

    /// <summary>Creates the pipeline.</summary>
    /// <param name="cache">The two-level link cache.</param>
    /// <param name="links">The link store behind the cache.</param>
    /// <param name="domains">Per-host runtime configuration, read only for HTML responses.</param>
    /// <param name="classifier">Client classifier.</param>
    /// <param name="botVerifier">Crawler confirmation (FR-161, TC-107).</param>
    /// <param name="routing">Rule evaluator.</param>
    /// <param name="sink">Bounded, non-blocking click event sink (FR-165, NFR-06).</param>
    /// <param name="clickIds">Click identifier codec.</param>
    /// <param name="ipHasher">Address hasher; supplies both the click stream fields and the limiter key.</param>
    /// <param name="guard">Anti-enumeration budget (§E.9, T-07).</param>
    /// <param name="metrics">Edge instruments.</param>
    /// <param name="cacheOptions">Cache expiry policy.</param>
    /// <param name="options">Edge options; supply the preview switch (FR-166).</param>
    /// <param name="privacy">Deployment-wide ceiling on what may be derived from an address.</param>
    /// <param name="timeProvider">Clock (SHARED-KERNEL §17.2).</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public LinkResolver(
        HybridCache cache,
        ILinkStore links,
        IDomainConfigStore domains,
        IClientClassifier classifier,
        IBotVerifier botVerifier,
        IRoutingEngine routing,
        IClickEventSink sink,
        IClickIdCodec clickIds,
        IIpHasher ipHasher,
        NotFoundEnumerationGuard guard,
        EdgeMetrics metrics,
        EdgeCacheOptions cacheOptions,
        IOptions<EdgeOptions> options,
        IOptions<EdgePrivacyOptions> privacy,
        TimeProvider timeProvider,
        ILogger<LinkResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(domains);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(botVerifier);
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(clickIds);
        ArgumentNullException.ThrowIfNull(ipHasher);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(cacheOptions);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(privacy);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _cache = cache;
        _links = links;
        _domains = domains;
        _classifier = classifier;
        _botVerifier = botVerifier;
        _routing = routing;
        _sink = sink;
        _clickIds = clickIds;
        _ipHasher = ipHasher;
        _guard = guard;
        _metrics = metrics;
        _cacheOptions = cacheOptions;
        _options = options.Value;
        _privacy = privacy.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Resolves one request.
    /// </summary>
    /// <param name="context">The incoming request.</param>
    /// <param name="rawSlug">The single path segment, exactly as routed.</param>
    /// <param name="cancellationToken">Cancellation for the cache and database reads.</param>
    /// <returns>The response.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public async Task<IResult> ResolveAsync(HttpContext context, string rawSlug, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        long started = Stopwatch.GetTimestamp();

        using Activity? span = EdgeActivitySource.StartResolve();

        DateTimeOffset now = _timeProvider.GetUtcNow();
        IPAddress? remoteIp = ClientRequestFactory.Canonical(context.Connection.RemoteIpAddress);
        string networkKey = EdgeRateLimitPartitions.NetworkKey(_ipHasher, remoteIp);

        if (!HostNormalizer.TryNormalize(context.Request.Host.Host, out string host))
        {
            // The request never named anything addressable, so there is no budget to charge and no
            // click to record.
            EdgeLog.NotServed(_logger, "-", NotFoundReasons.UnusableHost);
            Record(started, ResolveOutcomes.NotFound, CacheLevels.None, ClientContext.Empty, DecisionNames.NotFound);

            return InterstitialResults.NotFound(language: null);
        }

        // The only short circuit in the pipeline, and §E.9 requires it: a shadow banned prefix is
        // answered without a cache read or a query, and sees the same document as every other miss.
        if (_guard.IsShadowBanned(networkKey))
        {
            EdgeLog.NotServed(_logger, host, NotFoundReasons.ShadowBanned);
            Record(started, ResolveOutcomes.NotFound, CacheLevels.None, ClientContext.Empty, DecisionNames.NotFound);

            return InterstitialResults.NotFound(language: null);
        }

        // TC-109: a slug carrying homoglyphs or characters outside the alphabet cannot name a link, so
        // it is answered like any other miss rather than being handed to the store.
        bool slugUsable = SlugPolicy.TryNormalize(rawSlug, out string slug) && !SlugPolicy.IsReserved(slug);

        LinkSnapshot? link = null;
        bool factoryRan = false;

        if (slugUsable)
        {
            using Activity? lookupSpan = EdgeActivitySource.StartLookup();

            LookupResult result = await LookupAsync(host, slug, cancellationToken);

            if (result.Failed)
            {
                Record(started, ResolveOutcomes.Unavailable, CacheLevels.Miss, ClientContext.Empty, DecisionNames.NotFound);
                return DependencyUnavailable();
            }

            link = result.Link;
            factoryRan = result.FactoryRan;
        }

        string cacheLevel = !slugUsable ? CacheLevels.None : factoryRan ? CacheLevels.Miss : CacheLevels.Local;

        ClientContext client = await ClassifyAsync(context, host, now, networkKey, cancellationToken);

        ConsentSignal? signal = ConsentSignalReader.Read(context.Request, now);

        ConsentDecision consent = ConsentGate.Evaluate(
            link?.TenantConsentMode ?? ConsentMode.AggregateOnly,
            link?.DomainConsentMode,
            signal);

        string clickId = _clickIds.New(now);

        LinkServeState state = link is null ? LinkServeState.NotFound : link.GetServeState(now);

        RoutingDecision decision;

        using (Activity? routeSpan = EdgeActivitySource.StartRoute())
        {
            decision = state == LinkServeState.Servable && link is not null
                ? _routing.Evaluate(link.RoutingRules, client, consent, clickId)
                : ServeStateDecision(state);
        }

        ResolveOutcome outcome;

        using (Activity? renderSpan = EdgeActivitySource.StartRender())
        {
            outcome = await RespondAsync(
                context, host, slug, slugUsable, link, state, decision, client, consent, clickId, cancellationToken);

            outcome = ChargeNotFoundBudget(outcome, client, networkKey);
        }

        double elapsedSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;

        // FR-166: preview runs the whole pipeline and records nothing, so a rule can be debugged
        // against production data without moving a campaign's numbers.
        if (!IsPreview(context.Request))
        {
            WriteClickEvent(link, client, consent, decision, outcome.DecisionName, clickId, now, networkKey, remoteIp, elapsedSeconds);
        }

        if (outcome.Reason is { } reason)
        {
            EdgeLog.NotServed(_logger, host, reason);
        }
        else
        {
            EdgeLog.Resolved(_logger, host, slugUsable ? slug : "-", outcome.DecisionName, cacheLevel, elapsedSeconds * 1000d);
        }

        _metrics.RecordResolve(
            elapsedSeconds,
            outcome.Metric,
            cacheLevel,
            ChannelNames.From(client.Channel),
            outcome.DecisionName,
            client.Platform,
            client.IsCrawler);

        return outcome.Result;
    }

    /// <summary>
    /// Charges a 404 to the enumeration budget and switches to 429 for the request that drains it
    /// (§E.9, T-07, TC-108).
    /// </summary>
    /// <remarks>
    /// The budget is charged after the response has been decided rather than in middleware, because it
    /// is a limit on the <em>outcome</em>: middleware runs before the handler and cannot know whether a
    /// request will find a link. It is a separate partition from the sliding window over all resolves,
    /// which is what keeps a legitimate campaign spike from exhausting the anti-enumeration budget and
    /// switching the defence off exactly when the service is most worth scanning.
    /// </remarks>
    private ResolveOutcome ChargeNotFoundBudget(ResolveOutcome outcome, ClientContext client, string networkKey)
    {
        if (!string.Equals(outcome.Metric, ResolveOutcomes.NotFound, StringComparison.Ordinal))
        {
            return outcome;
        }

        if (_guard.Register(networkKey) == NotFoundVerdict.WithinBudget)
        {
            return outcome;
        }

        return outcome with
        {
            Result = InterstitialResults.TooManyRequests(client, domain: null, _guard.ShadowBanWindow),
            Metric = ResolveOutcomes.RateLimited,
        };
    }

    /// <summary>
    /// The stampede-safe lookup of §C.3.1: one cache read that falls through to at most one database
    /// query however many requests arrive for the same cold key at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The factory is a <c>static</c> lambda over an explicit state object, so the delegate is cached
    /// once in a static field and no closure is allocated per request. The one small object the state
    /// carries is a flag the factory sets, which is what tells the caller whether the request reached
    /// the database — the difference between the cache-hit bucket of NFR-01 and the miss bucket of
    /// NFR-02 being measured rather than guessed.
    /// </para>
    /// <para>
    /// A miss is always re-stamped, whether or not it found anything, and both branches cost one cache
    /// write. A found link is re-stamped so that it also carries its tenant tag, which is what makes
    /// <c>ILinkCacheInvalidator.InvalidateTenantAsync</c> reach it — the tenant is not known until the
    /// row has been read, so it cannot be a tag on the original write. A miss that found nothing is
    /// re-stamped with the short negative expiry of <c>Dle:Edge:Cache:NegativeSeconds</c>: caching
    /// misses is not optional, since an enumeration scan is almost entirely misses and re-querying for
    /// each one turns the scan into a denial of service, but a cached miss must not outlive the
    /// creation of the link somebody is about to publish, and the ten-minute positive default would.
    /// </para>
    /// </remarks>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "§D.6 requires that an unavailable database produces 503 and never 500, and that cached links " +
                        "keep resolving. The failure is logged and the branch returns an explicit unavailable result, " +
                        "which satisfies SHARED-KERNEL §17.9.")]
    private async ValueTask<LookupResult> LookupAsync(string host, string slug, CancellationToken cancellationToken)
    {
        var state = new LookupState(_links, host, slug);
        string key = LinkCacheKeys.Link(host, slug);

        try
        {
            _metrics.RecordLookup();

            LinkSnapshot? link = await _cache.GetOrCreateAsync(
                key,
                state,
                static (s, token) =>
                {
                    s.FactoryRan = true;
                    return s.Store.FindAsync(s.Host, s.Slug, token);
                },
                _cacheOptions.DefaultEntryOptions,
                tags: [LinkCacheKeys.HostTag(host)],
                cancellationToken: cancellationToken);

            if (state.FactoryRan)
            {
                _metrics.RecordFactoryRun();

                if (link is null)
                {
                    await _cache.SetAsync<LinkSnapshot?>(
                        key,
                        null,
                        _cacheOptions.NegativeEntryOptions,
                        tags: [LinkCacheKeys.HostTag(host)],
                        cancellationToken: cancellationToken);
                }
                else
                {
                    await _cache.SetAsync(
                        key,
                        link,
                        _cacheOptions.DefaultEntryOptions,
                        tags: [LinkCacheKeys.HostTag(host), LinkCacheKeys.TenantTag(link.TenantId)],
                        cancellationToken: cancellationToken);
                }
            }

            return new LookupResult(link, state.FactoryRan, Failed: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            EdgeLog.LookupFailed(_logger, host, exception);
            return new LookupResult(null, state.FactoryRan, Failed: true);
        }
    }

    /// <summary>
    /// Classifies the client and, when the user agent claims to be a crawler, confirms the claim
    /// (FR-161, TC-107).
    /// </summary>
    /// <remarks>
    /// An unconfirmed claim is not merely ignored: the context is rewritten so the request is routed as
    /// the ordinary browser it actually is, recorded with <c>is_bot=false</c> and
    /// <c>spoofed_bot=true</c>, and answered with a redirect rather than an Open Graph document. That
    /// combination is what TC-107 asserts, and it is what stops a forged user agent from being a free
    /// switch for keeping clicks out of — or pushing them into — somebody's campaign numbers.
    /// </remarks>
    private async ValueTask<ClientContext> ClassifyAsync(
        HttpContext context,
        string host,
        DateTimeOffset now,
        string networkKey,
        CancellationToken cancellationToken)
    {
        using Activity? span = EdgeActivitySource.StartClassify();

        ClientRequest request = ClientRequestFactory.Create(context, host, now);
        ClientContext client = _classifier.Classify(request);

        if (!client.IsCrawler || client.CrawlerName is not { Length: > 0 } crawler)
        {
            return client;
        }

        if (await _botVerifier.IsGenuineAsync(crawler, client.RemoteIp, cancellationToken))
        {
            return client;
        }

        EdgeLog.SpoofedBot(_logger, crawler, networkKey);

        return client with
        {
            IsCrawler = false,
            IsSpoofedBot = true,
            CrawlerName = null,
            Channel = ClientChannel.Browser,
            DeviceClass = DeviceClass.Unknown,
        };
    }

    /// <summary>
    /// Turns the decision into a response (ADR-009).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every redirect is <c>permanent: false</c>, which is a 302 (ADR-009, FR-164,
    /// SHARED-KERNEL §17.1). A 301 is cached indefinitely by browsers and by every intermediary in
    /// between, which would freeze an A/B split, outlive a campaign's expiry and survive a change of
    /// target — the three things a link exists to be able to do.
    /// </para>
    /// <para>
    /// Every target passes through <see cref="SafeUrl.Web(string?)"/> on its way out even though
    /// <see cref="RoutingUrlBuilder"/> already refuses to take one from a request parameter. The
    /// duplication is deliberate: it is a second, independent check that a hand-edited rule, a
    /// corrupted row or a future change to the builder would have to get past before a
    /// <c>javascript:</c> target or a header-splitting newline could reach a <c>Location</c> header
    /// (SHARED-KERNEL §17.4, TC-164).
    /// </para>
    /// </remarks>
    private async ValueTask<ResolveOutcome> RespondAsync(
        HttpContext context,
        string host,
        string slug,
        bool slugUsable,
        LinkSnapshot? link,
        LinkServeState state,
        RoutingDecision decision,
        ClientContext client,
        ConsentDecision consent,
        string clickId,
        CancellationToken cancellationToken)
    {
        if (link is null)
        {
            return NotServed(client, slugUsable ? NotFoundReasons.NoSuchLink : NotFoundReasons.UnusableSlug);
        }

        switch (state)
        {
            case LinkServeState.Gone:
                // TC-103: a quarantined link is withdrawn, not missing. 410 says the difference, and the
                // page carries the route to contest it (§E.3).
                return await GoneAsync(host, client, ResolveOutcomes.Gone, cancellationToken);

            case LinkServeState.Expired:
                // TC-104: an expired link with a configured landing page redirects there; without one it
                // is indistinguishable from a link that never existed.
                return SafeUrl.Web(link.ExpiredUrl) is { } expiredUrl
                    ? new ResolveOutcome(Results.Redirect(expiredUrl, permanent: false), ResolveOutcomes.Redirect, DecisionNames.Web, Reason: null)
                    : NotServed(client, NotFoundReasons.Expired);

            case LinkServeState.NotYetActive:
                return NotServed(client, NotFoundReasons.NotYetActive);

            case LinkServeState.NotFound:
                return NotServed(client, NotFoundReasons.Inactive);

            case LinkServeState.Servable:
                break;

            default:
                return NotServed(client, NotFoundReasons.NoSuchLink);
        }

        // A confirmed crawler gets a document whatever the rules decided, because it does not follow a
        // redirect reliably and runs no script (ADR-009, FR-161, TC-106). The rules were still
        // evaluated, so the click stream records the variant that would have been served.
        if (client.IsCrawler)
        {
            return new ResolveOutcome(
                InterstitialResults.OgPreview(
                    link,
                    client,
                    await LoadDomainAsync(host, cancellationToken),
                    ShortLinkUrl.Build(host, slug),
                    RoutingUrlBuilder.BuildWebUrl(decision, link, client, clickId, consent)),
                ResolveOutcomes.Preview,
                DecisionNames.Preview,
                Reason: null);
        }

        switch (decision.Kind)
        {
            case DecisionKind.Blocked:
                return await GoneAsync(host, client, ResolveOutcomes.Blocked, cancellationToken);

            case DecisionKind.Interstitial:
                return await InterstitialAsync(host, link, decision, client, consent, clickId, cancellationToken);

            case DecisionKind.AppDirect:
            {
                // An app-only rule has no store fallback. When the deep link is an absolute web URL it is
                // a Universal or App Link that the operating system claims on its own, so a 302 is both
                // correct and the fastest path; a custom scheme is not something a browser follows
                // reliably, so that case goes through the page with a real anchor instead.
                string? deeplink = RoutingUrlBuilder.BuildDeeplinkUrl(decision, link, CustomScheme(link, client), clickId, consent);

                return SafeUrl.Web(deeplink) is { } universalLink
                    ? new ResolveOutcome(Results.Redirect(universalLink, permanent: false), ResolveOutcomes.Redirect, DecisionNames.AppOpen, Reason: null)
                    : await InterstitialAsync(host, link, decision, client, consent, clickId, cancellationToken);
            }

            case DecisionKind.Store:
            {
                string target = RoutingUrlBuilder.BuildStoreUrl(decision, link, client, clickId, consent);

                return SafeUrl.Web(target) is { } storeUrl
                    ? new ResolveOutcome(
                        Results.Redirect(storeUrl, permanent: false),
                        ResolveOutcomes.Redirect,
                        DecisionNames.From(DecisionKind.Store, client.Platform),
                        Reason: null)
                    : NotServed(client, NotFoundReasons.NoMatchingRule);
            }

            case DecisionKind.Web:
            {
                string target = RoutingUrlBuilder.BuildWebUrl(decision, link, client, clickId, consent);

                return SafeUrl.Web(target) is { } webUrl
                    ? new ResolveOutcome(Results.Redirect(webUrl, permanent: false), ResolveOutcomes.Redirect, DecisionNames.Web, Reason: null)
                    : NotServed(client, NotFoundReasons.NoMatchingRule);
            }

            case DecisionKind.Gone:
                return await GoneAsync(host, client, ResolveOutcomes.Gone, cancellationToken);

            case DecisionKind.Preview:
            case DecisionKind.NotFound:
            default:
                // No rule matched and the set carried no default. The validator refuses to store such a
                // set (FR-127, TC-105), so reaching this is a data integrity problem rather than a
                // client one — and it still ends in the deny-by-default answer (SHARED-KERNEL §17.9).
                return NotServed(client, NotFoundReasons.NoMatchingRule);
        }
    }

    private async ValueTask<ResolveOutcome> GoneAsync(
        string host,
        ClientContext client,
        string metric,
        CancellationToken cancellationToken) =>
        new(
            InterstitialResults.Gone(client, await LoadDomainAsync(host, cancellationToken)),
            metric,
            string.Equals(metric, ResolveOutcomes.Blocked, StringComparison.Ordinal) ? DecisionNames.Blocked : DecisionNames.Gone,
            Reason: null);

    private async ValueTask<ResolveOutcome> InterstitialAsync(
        string host,
        LinkSnapshot link,
        RoutingDecision decision,
        ClientContext client,
        ConsentDecision consent,
        string clickId,
        CancellationToken cancellationToken)
    {
        bool storeBound = decision.Action is RoutingActionKind.AppOrStore or RoutingActionKind.StoreOnly;

        string fallback = storeBound
            ? RoutingUrlBuilder.BuildStoreUrl(decision, link, client, clickId, consent)
            : RoutingUrlBuilder.BuildWebUrl(decision, link, client, clickId, consent);

        return new ResolveOutcome(
            InterstitialResults.Page(
                link,
                decision,
                client,
                fallback,
                RoutingUrlBuilder.BuildDeeplinkUrl(decision, link, CustomScheme(link, client), clickId, consent),
                await LoadDomainAsync(host, cancellationToken)),
            ResolveOutcomes.Interstitial,
            DecisionNames.Interstitial,
            Reason: null);
    }

    /// <summary>
    /// Reads the link domain's runtime configuration, which supplies branding, the default language and
    /// the fallback Open Graph metadata for the pages that need them.
    /// </summary>
    /// <remarks>
    /// Only an HTML response calls this, and the 404 page deliberately does not: a scan is answered with
    /// the identical unbranded document every time, so the enumeration path stays at one cache lookup
    /// and nothing about the response varies with what exists on the host.
    /// </remarks>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Branding is cosmetic and must never fail a response. The failure is logged and the branch " +
                        "returns no configuration, which renders the neutral page — the explicit fallback required by " +
                        "SHARED-KERNEL §17.9.")]
    private async ValueTask<DomainRuntimeConfig?> LoadDomainAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            return await _cache.GetOrCreateAsync(
                LinkCacheKeys.Domain(host),
                (Store: _domains, Host: host),
                static (state, token) => state.Store.GetDomainAsync(state.Host, token),
                _cacheOptions.DefaultEntryOptions,
                tags: [LinkCacheKeys.HostTag(host)],
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            EdgeLog.LookupFailed(_logger, host, exception);
            return null;
        }
    }

    /// <summary>
    /// Builds the 404 response.
    /// </summary>
    /// <remarks>
    /// Composed identically for every reason. The reason travels to the debug log and to the click
    /// stream's <c>decision</c> column, and reaches the client in no form at all (T-07, TC-102, TC-166).
    /// </remarks>
    private static ResolveOutcome NotServed(ClientContext client, string reason) =>
        new(InterstitialResults.NotFound(client), ResolveOutcomes.NotFound, DecisionNames.NotFound, reason);

    /// <summary>
    /// Queues the click event without blocking the response (FR-165, NFR-06, §C.3.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sink is a bounded channel with <c>DropWrite</c>: when the analytics layer falls behind the
    /// telemetry is thrown away and the redirect is still served. That trade is what NFR-06 states, and
    /// it is why this is <c>TryWrite</c> and not an <c>await</c>.
    /// </para>
    /// <para>
    /// What is written is the intersection of two independent ceilings: the deployment's
    /// <c>Dle:Privacy:IpStorage</c> and the per-request consent decision. Neither widens the other, so
    /// an operator who configured <c>none</c> stores nothing even for a fully consenting visitor, and a
    /// visitor who refused attribution has no prefix stored however permissive the deployment is
    /// (§E.6.2, TC-145, TC-146).
    /// </para>
    /// </remarks>
    private void WriteClickEvent(
        LinkSnapshot? link,
        ClientContext client,
        ConsentDecision consent,
        RoutingDecision decision,
        string decisionName,
        string clickId,
        DateTimeOffset now,
        string networkKey,
        IPAddress? remoteIp,
        double elapsedSeconds)
    {
        bool storeHash = consent.StoreIpHash && _privacy.AllowsIpHash && remoteIp is not null;

        bool storePrefix = consent.StoreIpPrefix
            && _privacy.AllowsIpPrefix
            && !string.Equals(networkKey, EdgeRateLimitPartitions.UnknownClientKey, StringComparison.Ordinal);

        var clickEvent = new ClickEvent
        {
            Id = Guid.CreateVersion7(now),
            OccurredAt = now,
            TenantId = link?.TenantId ?? Guid.Empty,
            LinkId = link?.Id ?? 0L,
            ClickId = clickId,
            IpHash = storeHash ? _ipHasher.Hash(remoteIp!, now) : null,
            IpPrefix = storePrefix ? networkKey : null,
            UaFamily = client.UaFamily,
            OsFamily = client.OsFamily,
            OsVersion = client.OsVersion,
            DeviceClass = DeviceClassName(client.DeviceClass),
            Country = client.Country,
            Region = client.Region,
            Language = client.Language,
            ReferrerHost = client.ReferrerHost,
            Channel = ChannelNames.From(client.Channel),
            Decision = decisionName,
            AbBucket = decision.AbBucket,
            ConsentMode = ConsentModeName(consent.EffectiveMode),
            IsBot = client.IsCrawler,
            SpoofedBot = client.IsSpoofedBot,
            LatencyMs = (short)Math.Clamp(Math.Round(elapsedSeconds * 1000d), 0d, short.MaxValue),
        };

        if (!_sink.TryWrite(clickEvent))
        {
            EdgeLog.ClickEventDropped(_logger, _sink.DroppedCount);
        }
    }

    /// <summary>Whether the request asked for a decision preview (FR-166).</summary>
    private bool IsPreview(HttpRequest request) =>
        request.Query.TryGetValue(_options.PreviewQueryKey, out StringValues values) &&
        values.Count > 0 &&
        string.Equals(values[0], _options.PreviewQueryValue, StringComparison.Ordinal);

    /// <summary>Records a resolve that ended before the pipeline reached a decision.</summary>
    private void Record(long started, string metric, string cacheLevel, ClientContext client, string decisionName) =>
        _metrics.RecordResolve(
            Stopwatch.GetElapsedTime(started).TotalSeconds,
            metric,
            cacheLevel,
            ChannelNames.From(client.Channel),
            decisionName,
            client.Platform,
            client.IsCrawler);

    /// <summary>
    /// The 503 answer for a lookup that could not complete (§D.6).
    /// </summary>
    /// <remarks>
    /// 503 and never 500: the request was well formed and the service is temporarily unable to answer
    /// it, which is a difference every load balancer and every client retry policy acts on. Cached
    /// links keep resolving throughout, which is the property the chaos test asserts.
    /// </remarks>
    private static IResult DependencyUnavailable() =>
        Results.Problem(
            title: "The link store is temporarily unavailable.",
            statusCode: StatusCodes.Status503ServiceUnavailable,
            type: ProblemCodes.DependencyUnavailable);

    /// <summary>Maps a non-servable state onto the decision the click stream records.</summary>
    private static RoutingDecision ServeStateDecision(LinkServeState state) =>
        state == LinkServeState.Gone ? RoutingDecision.Gone : RoutingDecision.NotFound;

    /// <summary>The custom URI scheme of the platform the client is on, if the link declares one.</summary>
    private static string? CustomScheme(LinkSnapshot link, ClientContext client) => client.Platform switch
    {
        Platform.Ios => link.IosCustomScheme,
        Platform.Android => link.AndroidCustomScheme,
        _ => null,
    };

    /// <summary>Lowercase device class names, as §B.5.3 spells them in the click stream.</summary>
    private static string DeviceClassName(DeviceClass deviceClass) => deviceClass switch
    {
        DeviceClass.Phone => "phone",
        DeviceClass.Tablet => "tablet",
        DeviceClass.Desktop => "desktop",
        DeviceClass.Bot => "bot",
        _ => "unknown",
    };

    /// <summary>Lowercase consent mode names, as §B.5.3 spells them in the click stream.</summary>
    private static string ConsentModeName(ConsentMode mode) => mode switch
    {
        ConsentMode.Full => "full",
        ConsentMode.AggregateOnly => "aggregate_only",
        _ => "off",
    };

    /// <summary>
    /// State handed to the cache factory so that the factory delegate can stay <c>static</c>.
    /// </summary>
    /// <remarks>
    /// A class rather than a tuple only because the factory has to report back that it ran. Everything
    /// else it needs is immutable and travels in the same object, so the request allocates this one
    /// small instance and no closure (§C.3.1).
    /// </remarks>
    private sealed class LookupState(ILinkStore store, string host, string slug)
    {
        internal ILinkStore Store { get; } = store;

        internal string Host { get; } = host;

        internal string Slug { get; } = slug;

        internal bool FactoryRan { get; set; }
    }

    /// <summary>Result of the cache lookup.</summary>
    private readonly record struct LookupResult(LinkSnapshot? Link, bool FactoryRan, bool Failed);

    /// <summary>The response plus everything the metrics, the log and the click stream need about it.</summary>
    private readonly record struct ResolveOutcome(IResult Result, string Metric, string DecisionName, string? Reason);
}
