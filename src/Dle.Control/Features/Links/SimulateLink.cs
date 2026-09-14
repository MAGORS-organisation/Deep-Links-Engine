using Dle.Control.Features.Shared;
using Dle.Control.Infrastructure;

namespace Dle.Control.Features.Links;

/// <summary>
/// Answers "what happens for an iPhone 15 on iOS 18 in Slovakia, opened from Instagram" without
/// generating a click (FR-129).
/// </summary>
/// <remarks>
/// <para>
/// The decision itself comes from <see cref="IRoutingEngine"/> — the very engine the edge runs — so
/// the simulator can never disagree with production about which rule wins. What this class adds is
/// the part the engine has no reason to produce on the hot path: a sentence per rule saying why it
/// matched or why it lost. The predicates are re-evaluated here for that explanation only, using
/// the same public predicate types the engine uses, and the explanation is never allowed to
/// contradict the engine's own answer: the winning rule is whichever rule the engine returned.
/// </para>
/// <para>
/// Nothing is written. No click event, no rate-limited resolve, no attribution — which is what makes
/// the endpoint safe to call from a UI on every keystroke of a rule editor.
/// </para>
/// </remarks>
public static class SimulateLink
{
    /// <summary>Click identifier used when the caller supplies none, so a simulation is reproducible.</summary>
    private const string SampleClickId = "simulation";

    /// <summary>Runs the simulation.</summary>
    /// <param name="id">The link identifier, as text because identifiers are 64 bit.</param>
    /// <param name="request">The client to simulate.</param>
    /// <param name="acceptLanguage">Language of the explanation (NFR-15).</param>
    /// <param name="links">Link storage.</param>
    /// <param name="domains">Domain storage, used to resolve the serving host.</param>
    /// <param name="engine">The routing engine the edge itself runs.</param>
    /// <param name="classifier">
    /// The edge client classifier, when this deployment co-hosts one. Absent in the ordinary
    /// two-process topology, in which case the explicit fields of the request are used and a
    /// deliberately simple user agent heuristic fills the gaps.
    /// </param>
    /// <param name="timeProvider">Clock, for the default evaluation instant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The decision and the per-rule trace, or 404 when there is no such link.</returns>
    public static async Task<IResult> HandleAsync(
        string id,
        SimulateRequest request,
        string? acceptLanguage,
        LinkRepository links,
        DomainRepository domains,
        IRoutingEngine engine,
        IClientClassifier? classifier,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(domains);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (!long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out long linkId))
        {
            return DleProblem.NotFound("There is no link with that identifier.");
        }

        // The tenant query filter is what makes a foreign identifier indistinguishable from an
        // unknown one, so no ownership comparison appears here (TC-166, SHARED-KERNEL §17.7).
        Link? link = await links.GetAsync(linkId, includeQuarantined: true, cancellationToken);

        if (link is null)
        {
            return DleProblem.NotFound("There is no link with that identifier.");
        }

        LinkDomain? domain = await domains.GetAsync(link.DomainId, cancellationToken);

        if (domain is null)
        {
            return DleProblem.NotFound("There is no link with that identifier.");
        }

        LinkSnapshot? snapshot = await links.GetSnapshotAsync(domain.Host, link.Slug, cancellationToken);

        if (snapshot is null)
        {
            return DleProblem.NotFound("There is no link with that identifier.");
        }

        SimulationVocabulary vocabulary = SimulationVocabulary.ForRequest(acceptLanguage);
        DateTimeOffset at = request.At ?? timeProvider.GetUtcNow();
        ClientContext client = BuildClient(request, classifier, at);
        string clickId = string.IsNullOrWhiteSpace(request.ClickId) ? SampleClickId : request.ClickId.Trim();

        // The simulation assumes a visitor who accepted, so that the operator sees the fullest URL
        // the tenant's own configuration permits. The effective mode is reported back, which is what
        // tells them whether that assumption is reachable at all (§E.6.2).
        ConsentDecision consent = ConsentGate.Evaluate(
            snapshot.TenantConsentMode,
            snapshot.DomainConsentMode,
            new ConsentSignal { Analytics = true, Attribution = true, Source = "simulation", Timestamp = at });

        IReadOnlyList<RoutingRule> rules = snapshot.RoutingRules;
        RoutingDecision decision = engine.Evaluate(rules, client, consent, clickId);

        List<string> trace = BuildTrace(rules, client, at, clickId, decision, vocabulary);

        string? webUrl = decision.Kind is DecisionKind.NotFound or DecisionKind.Blocked
            ? null
            : RoutingUrlBuilder.BuildWebUrl(decision, snapshot, client, clickId, consent);

        string? storeUrl = decision.Kind is DecisionKind.Store or DecisionKind.Interstitial
            ? RoutingUrlBuilder.BuildStoreUrl(decision, snapshot, client, clickId, consent)
            : null;

        return TypedResults.Ok(new SimulateResponse
        {
            MatchedRuleId = decision.MatchedRuleId,
            Decision = DecisionNames.From(decision.Kind, client.Platform),
            Url = webUrl,
            DeeplinkPath = decision.DeeplinkPath ?? snapshot.DeeplinkPath,
            StoreUrl = storeUrl,
            AbBucket = decision.AbBucket ?? ConsistentBucket.Of(clickId),
            AbVariant = decision.AbVariant,
            ConsentMode = ConsentModeName(consent.EffectiveMode),
            Trace = trace,
        });
    }

    /// <summary>Builds the client the rules are evaluated against.</summary>
    /// <param name="request">The simulation request.</param>
    /// <param name="classifier">The edge classifier when one is available.</param>
    /// <param name="at">The instant to evaluate at.</param>
    /// <returns>The classified client.</returns>
    /// <remarks>
    /// Explicit fields always win over anything derived from the user agent. A marketer testing "what
    /// happens in Germany" has said what they mean, and a user agent string pasted alongside must not
    /// silently override it.
    /// </remarks>
    private static ClientContext BuildClient(
        SimulateRequest request,
        IClientClassifier? classifier,
        DateTimeOffset at)
    {
        ClientContext derived = ClassifyUserAgent(request.UserAgent, classifier, at);

        Platform platform = ParsePlatform(request.Platform) ?? derived.Platform;
        ClientChannel channel = string.IsNullOrWhiteSpace(request.Channel)
            ? derived.Channel
            : ChannelNames.Parse(request.Channel);

        string? osVersion = Normalize(request.OsVersion) ?? derived.OsVersion;
        string? appVersion = Normalize(request.AppVersion);

        return new ClientContext
        {
            Platform = platform,
            DeviceClass = DeviceClassFor(platform),
            Channel = channel,
            UaFamily = derived.UaFamily,
            OsFamily = derived.OsFamily,
            OsVersion = osVersion,
            AppVersion = appVersion,
            Country = string.IsNullOrWhiteSpace(request.Country)
                ? null
                : request.Country.Trim().ToUpperInvariant(),
            Region = string.IsNullOrWhiteSpace(request.Region) ? null : request.Region.Trim(),
            Language = string.IsNullOrWhiteSpace(request.Language)
                ? null
                : request.Language.Trim().ToLowerInvariant(),
            IsCrawler = channel == ClientChannel.Crawler,
            ReceivedAt = at,
        };
    }

    /// <summary>
    /// Classifies a pasted user agent, using the edge classifier when the process has one.
    /// </summary>
    /// <param name="userAgent">The user agent string, or <see langword="null"/>.</param>
    /// <param name="classifier">The edge classifier, when registered.</param>
    /// <param name="at">The instant to stamp on the synthetic request.</param>
    /// <returns>The derived context, or an empty one.</returns>
    /// <remarks>
    /// The fallback heuristic is deliberately shallow. Real classification — the crawler catalogue,
    /// the in-app webview catalogue, reverse DNS bot verification — belongs to the edge and stays
    /// there; duplicating it here would create a second implementation that quietly drifts, and a
    /// simulator that disagrees with production is worse than no simulator. What is left is enough to
    /// turn a pasted user agent into a platform, and the operator can always state the platform
    /// outright.
    /// </remarks>
    private static ClientContext ClassifyUserAgent(
        string? userAgent,
        IClientClassifier? classifier,
        DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return ClientContext.Empty;
        }

        if (classifier is not null)
        {
            return classifier.Classify(new ClientRequest
            {
                Host = "simulation.invalid",
                Path = "/",
                UserAgent = userAgent,
                ReceivedAt = at,
            });
        }

        Platform platform = Platform.Unknown;
        string? osFamily = null;

        if (userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("iPad", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains(" iOS", StringComparison.OrdinalIgnoreCase))
        {
            platform = Platform.Ios;
            osFamily = "iOS";
        }
        else if (userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase))
        {
            platform = Platform.Android;
            osFamily = "Android";
        }
        else if (userAgent.Contains("Windows", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("Macintosh", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("X11", StringComparison.OrdinalIgnoreCase))
        {
            platform = Platform.Desktop;
            osFamily = "Desktop";
        }

        return new ClientContext
        {
            Platform = platform,
            DeviceClass = DeviceClassFor(platform),
            Channel = ClientChannel.Browser,
            OsFamily = osFamily,
            ReceivedAt = at,
        };
    }

    /// <summary>
    /// Explains every rule in evaluation order, so a marketer can see why rule 2 lost (FR-129).
    /// </summary>
    /// <param name="rules">The rule set.</param>
    /// <param name="client">The simulated client.</param>
    /// <param name="at">The instant time windows are evaluated against.</param>
    /// <param name="clickId">The click identifier the A/B bucket is derived from.</param>
    /// <param name="decision">The engine's decision, which settles which rule actually won.</param>
    /// <param name="words">The vocabulary of the caller's language.</param>
    /// <returns>One line per rule.</returns>
    private static List<string> BuildTrace(
        IReadOnlyList<RoutingRule> rules,
        ClientContext client,
        DateTimeOffset at,
        string clickId,
        RoutingDecision decision,
        SimulationVocabulary words)
    {
        if (rules.Count == 0)
        {
            return [words.NoRules];
        }

        short bucket = ConsistentBucket.Of(clickId);
        List<string> trace = new(rules.Count + 1);
        int winner = -1;

        for (int i = 0; i < rules.Count; i++)
        {
            RoutingRule rule = rules[i];
            string number = (i + 1).ToString(CultureInfo.InvariantCulture);
            string ruleId = rule?.Id ?? string.Empty;

            if (rule is null || rule.Then is null)
            {
                trace.Add(SimulationVocabulary.Format(
                    words.NotMatched, number, ruleId, "the rule has no action"));

                continue;
            }

            if (winner >= 0)
            {
                trace.Add(SimulationVocabulary.Format(
                    words.NotEvaluated,
                    number,
                    ruleId,
                    (winner + 1).ToString(CultureInfo.InvariantCulture)));

                continue;
            }

            if (rule.When is null)
            {
                winner = i;
                trace.Add(SimulationVocabulary.Format(words.DefaultMatched, number, ruleId));
                continue;
            }

            string? reason = Explain(rule.When, client, at, bucket, words, out string? variant);

            if (reason is null)
            {
                winner = i;
                trace.Add(SimulationVocabulary.Format(words.Matched, number, ruleId));

                if (variant is not null)
                {
                    trace.Add(SimulationVocabulary.Format(
                        words.AbSelected, variant, bucket.ToString(CultureInfo.InvariantCulture)));
                }
            }
            else
            {
                trace.Add(SimulationVocabulary.Format(words.NotMatched, number, ruleId, reason));
            }
        }

        if (winner < 0 && decision.Kind == DecisionKind.NotFound)
        {
            trace.Add(words.NoMatch);
        }

        return trace;
    }

    /// <summary>
    /// Says why a condition did not hold, or <see langword="null"/> when it did.
    /// </summary>
    /// <param name="when">The condition.</param>
    /// <param name="client">The simulated client.</param>
    /// <param name="at">The instant time windows are evaluated against.</param>
    /// <param name="bucket">The A/B bucket derived from the click identifier.</param>
    /// <param name="words">The vocabulary of the caller's language.</param>
    /// <param name="variant">The A/B variant selected, when the condition declares a split.</param>
    /// <returns>The first failing reason, or <see langword="null"/>.</returns>
    /// <remarks>
    /// The order of the checks mirrors <c>RoutingEngine.Matches</c>, so the reason reported is the
    /// same one the engine stopped on. A condition field that is set while the client's counterpart
    /// is unknown never matches — a rule scoped to Slovakia must not fire for a client with no geo
    /// data — and the explanation says so in as many words.
    /// </remarks>
    private static string? Explain(
        RuleCondition when,
        ClientContext client,
        DateTimeOffset at,
        short bucket,
        SimulationVocabulary words,
        out string? variant)
    {
        variant = null;

        if (when.Platform is { Length: > 0 } platforms
            && !Contains(platforms, PlatformName(client.Platform)))
        {
            return SimulationVocabulary.Format(
                words.ReasonPlatform, PlatformName(client.Platform), Join(platforms));
        }

        if (when.Country is { Length: > 0 } countries && !Contains(countries, client.Country))
        {
            return SimulationVocabulary.Format(
                words.ReasonCountry, client.Country ?? words.Unknown, Join(countries));
        }

        if (when.Region is { Length: > 0 } regions && !Contains(regions, client.Region))
        {
            return SimulationVocabulary.Format(
                words.ReasonRegion, client.Region ?? words.Unknown, Join(regions));
        }

        if (when.Language is { Length: > 0 } languages && !ContainsLanguage(languages, client.Language))
        {
            return SimulationVocabulary.Format(
                words.ReasonLanguage, client.Language ?? words.Unknown, Join(languages));
        }

        if (when.Channel is { Length: > 0 } channels
            && !Contains(channels, ChannelNames.From(client.Channel)))
        {
            return SimulationVocabulary.Format(
                words.ReasonChannel, ChannelNames.From(client.Channel), Join(channels));
        }

        if (when.OsVersion is { } osVersion && !osVersion.Matches(client.OsVersion))
        {
            return SimulationVocabulary.Format(words.ReasonOsVersion, client.OsVersion ?? words.Unknown);
        }

        if (when.AppVersion is { } appVersion && !appVersion.Matches(client.AppVersion))
        {
            return SimulationVocabulary.Format(words.ReasonAppVersion, client.AppVersion ?? words.Unknown);
        }

        if (when.TimeWindow is { } window && !window.Matches(at))
        {
            return SimulationVocabulary.Format(
                words.ReasonTimeWindow, at.ToString("O", CultureInfo.InvariantCulture));
        }

        if (when.Ab is { Length: > 0 } ab && !TryPickVariant(ab, bucket, out variant))
        {
            return SimulationVocabulary.Format(
                words.ReasonAbBucket, bucket.ToString(CultureInfo.InvariantCulture));
        }

        return null;
    }

    /// <summary>Walks the A/B variants as consecutive ranges over 0 to 99.</summary>
    /// <param name="variants">The declared split.</param>
    /// <param name="bucket">The bucket derived from the click identifier.</param>
    /// <param name="variant">The selected variant.</param>
    /// <returns><see langword="true"/> when the bucket falls inside the declared share.</returns>
    private static bool TryPickVariant(AbVariant[] variants, short bucket, out string? variant)
    {
        int cumulative = 0;

        foreach (AbVariant candidate in variants)
        {
            if (candidate is null || candidate.Percent <= 0)
            {
                continue;
            }

            cumulative += candidate.Percent;

            if (bucket < cumulative)
            {
                variant = candidate.Variant;
                return true;
            }
        }

        variant = null;
        return false;
    }

    /// <summary>Membership test with the engine's own semantics: unknown never matches.</summary>
    /// <param name="candidates">The values the rule accepts.</param>
    /// <param name="actual">The client's value.</param>
    /// <returns><see langword="true"/> when the client's value is one of the candidates.</returns>
    private static bool Contains(string[] candidates, string? actual)
    {
        if (string.IsNullOrEmpty(actual))
        {
            return false;
        }

        foreach (string candidate in candidates)
        {
            if (string.Equals(candidate, actual, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Language membership, comparing primary subtags.</summary>
    /// <param name="candidates">The values the rule accepts.</param>
    /// <param name="actual">The client's language tag.</param>
    /// <returns><see langword="true"/> when the primary subtags agree.</returns>
    private static bool ContainsLanguage(string[] candidates, string? actual)
    {
        if (string.IsNullOrEmpty(actual))
        {
            return false;
        }

        ReadOnlySpan<char> primary = actual.AsSpan();
        int dash = primary.IndexOf('-');

        if (dash > 0)
        {
            primary = primary[..dash];
        }

        foreach (string candidate in candidates)
        {
            if (primary.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Renders a candidate list for a human explanation.</summary>
    /// <param name="values">The values the rule accepts.</param>
    /// <returns>The values, comma separated.</returns>
    private static string Join(string[] values) => string.Join(", ", values);

    /// <summary>Canonical lowercase name of a platform, as written in the JSON rule format.</summary>
    /// <param name="platform">The platform.</param>
    /// <returns>The name.</returns>
    private static string PlatformName(Platform platform) => platform switch
    {
        Platform.Ios => "ios",
        Platform.Android => "android",
        Platform.Desktop => "desktop",
        Platform.Other => "other",
        _ => "unknown",
    };

    /// <summary>Reads a platform name from the request.</summary>
    /// <param name="value">The value supplied.</param>
    /// <returns>The platform, or <see langword="null"/> when none was supplied.</returns>
    private static Platform? ParsePlatform(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "ios" => Platform.Ios,
            "android" => Platform.Android,
            "desktop" => Platform.Desktop,
            "other" => Platform.Other,
            _ => Platform.Unknown,
        };
    }

    /// <summary>Picks a plausible device class for a platform.</summary>
    /// <param name="platform">The platform.</param>
    /// <returns>The device class.</returns>
    private static DeviceClass DeviceClassFor(Platform platform) => platform switch
    {
        Platform.Ios or Platform.Android => DeviceClass.Phone,
        Platform.Desktop => DeviceClass.Desktop,
        _ => DeviceClass.Unknown,
    };

    /// <summary>Normalizes a version so that "18" and "18.0.0" compare equal.</summary>
    /// <param name="value">The version supplied.</param>
    /// <returns>The normalized version, or <see langword="null"/>.</returns>
    private static string? Normalize(string? value) =>
        VersionComparer.TryNormalize(value, out string normalized) ? normalized : null;

    /// <summary>Renders a consent mode as the name used on the wire.</summary>
    /// <param name="mode">The effective mode.</param>
    /// <returns>The name.</returns>
    private static string ConsentModeName(ConsentMode mode) => mode switch
    {
        ConsentMode.Full => "full",
        ConsentMode.AggregateOnly => "aggregate_only",
        _ => "off",
    };
}
