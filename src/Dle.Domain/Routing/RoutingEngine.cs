namespace Dle.Domain.Routing;

/// <summary>
/// The default <see cref="IRoutingEngine"/>: an ordered, first-match-wins evaluator over the rule set
/// (FR-127) with deterministic A/B bucketing (FR-125) and the ADR-009 response shape mapping.
/// </summary>
public sealed class RoutingEngine : IRoutingEngine
{
    private const string ClickIdPlaceholder = "{click_id}";

    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates the engine.
    /// </summary>
    /// <param name="timeProvider">
    /// Clock used for time-window predicates when the request itself does not carry a timestamp.
    /// Never <see cref="DateTime.UtcNow"/> — the clock has to be substitutable in tests and in the
    /// rule simulator (SHARED-KERNEL §0, §17.2).
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="timeProvider"/> is <see langword="null"/>.</exception>
    public RoutingEngine(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public RoutingDecision Evaluate(
        IReadOnlyList<RoutingRule> rules,
        ClientContext client,
        ConsentDecision consent,
        string clickId)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(consent);

        if (rules is null || rules.Count == 0)
        {
            return RoutingDecision.NotFound;
        }

        // The request's own arrival time is authoritative, so that a decision replayed from the click
        // stream and a decision made live for the same request agree. ClientContext.Empty and any
        // hand-built context without a timestamp fall back to the injected clock.
        DateTimeOffset now = client.ReceivedAt == default ? _timeProvider.GetUtcNow() : client.ReceivedAt;

        // Computed once per request: every rule's A/B split has to see the same bucket, otherwise a
        // client could slide between variants as it walks down the rule list.
        short bucket = ConsistentBucket.Of(clickId ?? string.Empty);

        for (int i = 0; i < rules.Count; i++)
        {
            RoutingRule rule = rules[i];

            if (rule is null || rule.Then is null)
            {
                continue;
            }

            string? variant = null;

            // A rule without a condition is the default rule: it matches everything.
            if (rule.When is not null && !Matches(rule.When, client, now, bucket, out variant))
            {
                continue;
            }

            return Build(rule, variant, bucket, client, consent);
        }

        return RoutingDecision.NotFound;
    }

    private static RoutingDecision Build(
        RoutingRule rule,
        string? variant,
        short bucket,
        ClientContext client,
        ConsentDecision consent) => new()
        {
            Kind = ResolveKind(rule.Then, client),
            MatchedRuleId = string.IsNullOrEmpty(rule.Id) ? string.Empty : rule.Id,
            Action = rule.Then.Action,
            DeeplinkPath = rule.Then.DeeplinkPath,
            WebUrl = rule.Then.Url,
            StoreUrl = rule.Then.StoreUrl,
            ReferrerTemplate = ApplyConsent(rule.Then.ReferrerTemplate, consent),
            Interstitial = rule.Then.Interstitial,
            AbVariant = variant,
            AbBucket = variant is null ? null : bucket,
        };

    /// <summary>
    /// Consent enters the pipeline here, not as a filter applied to the finished URL: if click-id
    /// linking is not permitted, every referrer pair carrying the click-id placeholder is dropped from
    /// the decision itself, so no downstream consumer — URL builder, interstitial, simulator — can emit
    /// it (§E.6.2, TC-145/TC-146).
    /// </summary>
    private static string? ApplyConsent(string? template, ConsentDecision consent)
    {
        if (string.IsNullOrEmpty(template) || consent.AllowClickIdLinking)
        {
            return template;
        }

        string[] segments = template.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var kept = new List<string>(segments.Length);

        foreach (string segment in segments)
        {
            if (!segment.Contains(ClickIdPlaceholder, StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(segment);
            }
        }

        return kept.Count == 0 ? null : string.Join('&', kept);
    }

    /// <summary>
    /// Maps the declared action onto the response class (ADR-009).
    /// </summary>
    private static DecisionKind ResolveKind(RuleAction action, ClientContext client) => action.Action switch
    {
        RoutingActionKind.Web => DecisionKind.Web,
        RoutingActionKind.StoreOnly => DecisionKind.Store,
        RoutingActionKind.AppOnly => DecisionKind.AppDirect,
        RoutingActionKind.Block => DecisionKind.Blocked,
        RoutingActionKind.AppOrStore => ResolveAppOrStore(action.Interstitial, client),

        // An action value outside the enum can only come from hand-edited data. Serving the web
        // fallback is the safe default: it never opens an application and never blocks a real user.
        _ => DecisionKind.Web,
    };

    private static DecisionKind ResolveAppOrStore(InterstitialMode mode, ClientContext client)
    {
        // An in-app webview only surfaces a Universal/App Link on a real tap, so it always gets the
        // interstitial — InterstitialMode.Never cannot switch that off (§A.2.6, FR-162).
        if (ChannelNames.IsInAppWebView(client.Channel) || mode == InterstitialMode.Always)
        {
            return DecisionKind.Interstitial;
        }

        if (mode == InterstitialMode.Never)
        {
            return DecisionKind.Store;
        }

        return client.Platform is Platform.Ios or Platform.Android
            ? DecisionKind.Interstitial
            : DecisionKind.Web;
    }

    private static bool Matches(
        RuleCondition when,
        ClientContext client,
        DateTimeOffset now,
        short bucket,
        out string? variant)
    {
        variant = null;

        if (when.Platform is { Length: > 0 } platforms && !ContainsValue(platforms, PlatformName(client.Platform)))
        {
            return false;
        }

        if (when.Country is { Length: > 0 } countries && !ContainsValue(countries, client.Country))
        {
            return false;
        }

        if (when.Region is { Length: > 0 } regions && !ContainsValue(regions, client.Region))
        {
            return false;
        }

        if (when.Language is { Length: > 0 } languages && !ContainsLanguage(languages, client.Language))
        {
            return false;
        }

        if (when.Channel is { Length: > 0 } channels && !ContainsValue(channels, ChannelNames.From(client.Channel)))
        {
            return false;
        }

        if (when.OsVersion is { } osVersion && !osVersion.Matches(client.OsVersion))
        {
            return false;
        }

        if (when.AppVersion is { } appVersion && !appVersion.Matches(client.AppVersion))
        {
            return false;
        }

        if (when.TimeWindow is { } window && !window.Matches(now))
        {
            return false;
        }

        if (when.Ab is { Length: > 0 } ab && !TryPickVariant(ab, bucket, out variant))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Walks the variants as consecutive percentage ranges over 0..99 and returns the one whose
    /// cumulative range contains the bucket. A bucket beyond the accumulated total does not match,
    /// which lets an author run a rule at, say, 20 % of traffic and let the rest fall through (FR-125).
    /// </summary>
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

    /// <summary>Canonical lowercase name of a platform, as written in the JSON rule format.</summary>
    private static string PlatformName(Platform platform) => platform switch
    {
        Platform.Ios => "ios",
        Platform.Android => "android",
        Platform.Desktop => "desktop",
        Platform.Other => "other",
        _ => "unknown",
    };

    /// <summary>
    /// OR-semantics membership test. A condition field that is set but whose client-side counterpart is
    /// unknown can never match: a rule scoped to Slovakia must not fire for a client with no geo data.
    /// </summary>
    private static bool ContainsValue(string[] candidates, string? actual)
    {
        if (string.IsNullOrEmpty(actual))
        {
            return false;
        }

        foreach (string candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate)
                && string.Equals(candidate.Trim(), actual, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Language matching compares primary subtags only, on both sides: a rule listing <c>"sk"</c> matches
    /// a client announcing <c>sk-SK</c>, and a rule sloppily written as <c>"sk-SK"</c> still matches
    /// a client announcing <c>sk</c> (FR-123).
    /// </summary>
    private static bool ContainsLanguage(string[] candidates, string? actual)
    {
        ReadOnlySpan<char> actualPrimary = PrimarySubtag(actual);

        if (actualPrimary.IsEmpty)
        {
            return false;
        }

        foreach (string candidate in candidates)
        {
            ReadOnlySpan<char> candidatePrimary = PrimarySubtag(candidate);

            if (!candidatePrimary.IsEmpty
                && candidatePrimary.Equals(actualPrimary, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static ReadOnlySpan<char> PrimarySubtag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return [];
        }

        ReadOnlySpan<char> span = tag.AsSpan().Trim();
        int separator = span.IndexOfAny('-', '_');

        return separator < 0 ? span : span[..separator];
    }
}
