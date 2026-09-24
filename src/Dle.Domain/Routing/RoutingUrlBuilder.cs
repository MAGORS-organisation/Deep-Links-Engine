using System.Collections.Frozen;
using System.Text;

namespace Dle.Domain.Routing;

/// <summary>
/// Turns a <see cref="RoutingDecision"/> into the concrete URLs the edge redirects to or renders on
/// the interstitial page (FR-128, §A.2.4).
/// </summary>
/// <remarks>
/// <para>
/// <b>The target never comes from the request.</b> Every URL produced here is rooted in a value that
/// an authenticated operator stored — the matched rule or the link snapshot. Parameters taken from the
/// incoming query string are restricted to <see cref="ForwardableQueryKeys"/>, are percent-encoded
/// before they are appended, and are only ever added to the query component; the scheme, host and path
/// of the target are copied verbatim and are structurally unreachable from request data (TC-164, §17.4).
/// </para>
/// <para>
/// <b>Consent is an input, not a post-filter.</b> The overloads that take a <see cref="ConsentDecision"/>
/// are the ones the edge uses. The four-argument overloads required by the shared kernel contract
/// default to a denied consent decision, so a caller that has not run the consent gate can never emit a
/// click-id parameter by accident (ePrivacy art. 5(3), §E.6.2, TC-145/TC-146).
/// </para>
/// </remarks>
public static class RoutingUrlBuilder
{
    /// <summary>
    /// Query parameters that may be carried from the incoming request into the target URL
    /// (allowlist, §E.6.3). Anything not listed here is dropped, which is what makes an open redirect
    /// through a query parameter impossible (TC-164).
    /// </summary>
    public static readonly string[] ForwardableQueryKeys =
    [
        "utm_source",
        "utm_medium",
        "utm_campaign",
        "utm_term",
        "utm_content",
        "gclid",
        "fbclid",
        "ttclid",
        "msclkid",
        "twclid",
        "li_fat_id",
        "igshid",
        "ref",
        ClickIdParameter,
    ];

    /// <summary>The query parameter carrying our own click identifier into the target and into the Play referrer.</summary>
    public const string ClickIdParameter = "dl_cid";

    /// <summary>The Play Store query parameter that carries the Install Referrer string (§A.2.4).</summary>
    public const string ReferrerParameter = "referrer";

    /// <summary>
    /// Working cap on the percent-encoded Play referrer. Google does not publish a maximum length;
    /// staying under 500 characters is the widely used safe bound (§A.2.4).
    /// </summary>
    public const int MaxEncodedReferrerLength = 500;

    /// <summary>
    /// Referrer template used when the matched rule does not declare one. It carries the click id, which is
    /// the only deterministic deferred-deep-link channel Android offers, plus the three core UTM fields.
    /// </summary>
    public const string DefaultReferrerTemplate =
        "dl_cid={click_id}&utm_source={utm_source}&utm_medium={utm_medium}&utm_campaign={utm_campaign}";

    private const string UtmSourceKey = "utm_source";
    private const string UtmMediumKey = "utm_medium";
    private const string UtmCampaignKey = "utm_campaign";
    private const string UtmTermKey = "utm_term";
    private const string UtmContentKey = "utm_content";

    private const string AppleCampaignParameter = "ct";

    private static readonly FrozenSet<string> Forwardable =
        ForwardableQueryKeys.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Schemes that must never be used for a custom-scheme deep link (T-01, §A.2.3).</summary>
    private static readonly FrozenSet<string> DeniedSchemes =
        new[] { "javascript", "data", "vbscript", "file", "blob", "about", "intent", "http", "https" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly ConsentDecision NoConsent = ConsentDecision.Denied("consent_not_supplied");

    /// <summary>
    /// Builds the store URL for the decision, without consent information.
    /// </summary>
    /// <param name="decision">The routing decision.</param>
    /// <param name="link">The link snapshot, used for the per-platform store URL and the UTM defaults.</param>
    /// <param name="client">The classified client; its platform selects the Play or App Store behaviour.</param>
    /// <param name="clickId">The click identifier for this request.</param>
    /// <returns>An absolute URL. Falls back to the web URL when no store URL is configured.</returns>
    /// <remarks>
    /// Consent defaults to denied, so this overload never emits a click-id parameter. Callers on the
    /// resolve path must use the overload that takes the decision produced by the consent gate.
    /// </remarks>
    public static string BuildStoreUrl(RoutingDecision decision, LinkSnapshot link, ClientContext client, string clickId) =>
        BuildStoreUrl(decision, link, client, clickId, NoConsent);

    /// <summary>
    /// Builds the store URL for the decision.
    /// </summary>
    /// <param name="decision">The routing decision.</param>
    /// <param name="link">The link snapshot, used for the per-platform store URL and the UTM defaults.</param>
    /// <param name="client">The classified client; its platform selects the Play or App Store behaviour.</param>
    /// <param name="clickId">The click identifier for this request.</param>
    /// <param name="consent">The consent decision; it gates the click id, nothing else.</param>
    /// <returns>An absolute URL. Falls back to the web URL when no store URL is configured.</returns>
    public static string BuildStoreUrl(
        RoutingDecision decision,
        LinkSnapshot link,
        ClientContext client,
        string clickId,
        ConsentDecision consent)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(consent);

        string? storeUrl = AbsoluteWebUrl(decision.StoreUrl) ?? client.Platform switch
        {
            Platform.Ios => AbsoluteWebUrl(link.IosStoreUrl),
            Platform.Android => AbsoluteWebUrl(link.AndroidStoreUrl),
            _ => null,
        };

        if (storeUrl is null)
        {
            // Nothing to send the client to in a store; the web fallback is always available.
            return BuildWebUrl(decision, link, client, clickId, consent);
        }

        UrlParts parts = Split(storeUrl);

        switch (client.Platform)
        {
            case Platform.Android:
                ApplyPlayReferrer(parts, decision, link, clickId, consent);
                break;

            case Platform.Ios:
                // iOS has no click-id channel through the App Store at all (§A.2.4); only the campaign
                // token travels, and only when the operator has not already set one.
                ApplyAppleCampaign(parts, link);
                break;

            default:
                break;
        }

        return Render(parts);
    }

    /// <summary>
    /// Builds the web fallback URL for the decision, without consent information.
    /// </summary>
    /// <param name="decision">The routing decision.</param>
    /// <param name="link">The link snapshot, supplying the target URL and the UTM defaults.</param>
    /// <param name="client">The classified client, supplying the forwardable request parameters.</param>
    /// <param name="clickId">The click identifier for this request.</param>
    /// <returns>The target URL with the merged query string.</returns>
    /// <remarks>
    /// Consent defaults to denied, so this overload never emits a click-id parameter. Callers on the
    /// resolve path must use the overload that takes the decision produced by the consent gate.
    /// </remarks>
    public static string BuildWebUrl(RoutingDecision decision, LinkSnapshot link, ClientContext client, string clickId) =>
        BuildWebUrl(decision, link, client, clickId, NoConsent);

    /// <summary>
    /// Builds the web fallback URL for the decision (FR-128).
    /// </summary>
    /// <param name="decision">The routing decision.</param>
    /// <param name="link">The link snapshot, supplying the target URL and the UTM defaults.</param>
    /// <param name="client">The classified client, supplying the forwardable request parameters.</param>
    /// <param name="clickId">The click identifier for this request.</param>
    /// <param name="consent">The consent decision; it gates the click id, nothing else.</param>
    /// <returns>The target URL with the merged query string.</returns>
    /// <remarks>
    /// Precedence, highest first: parameters already present on the target URL, then the forwardable
    /// request parameters, then the link's UTM defaults. The click id is written last and always wins,
    /// because a stale <c>dl_cid</c> arriving on the request must never be mistaken for this click.
    /// </remarks>
    public static string BuildWebUrl(
        RoutingDecision decision,
        LinkSnapshot link,
        ClientContext client,
        string clickId,
        ConsentDecision consent)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(consent);

        string target = AbsoluteWebUrl(decision.WebUrl) ?? AbsoluteWebUrl(link.TargetUrl) ?? link.TargetUrl;

        // Only the query component of `target` is ever touched below; its scheme, host and path are
        // copied verbatim into the result, which is what makes TC-164 structurally impossible.
        UrlParts parts = Split(target);

        var locked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, string?> pair in parts.Query)
        {
            locked.Add(pair.Key);
        }

        // 1. UTM defaults configured on the link. Parameters already on the target URL win.
        foreach (KeyValuePair<string, string> utm in link.Utm)
        {
            if (string.IsNullOrEmpty(utm.Key) || locked.Contains(utm.Key))
            {
                continue;
            }

            Set(parts.Query, Uri.EscapeDataString(utm.Key), Uri.EscapeDataString(utm.Value ?? string.Empty));
        }

        // 2. Allowlisted parameters from the incoming request. They describe the actual traffic source,
        //    so they override the static UTM defaults — but never a parameter the operator put on the
        //    target URL itself, and never anything outside the allowlist.
        foreach (KeyValuePair<string, string> query in client.Query)
        {
            if (string.IsNullOrEmpty(query.Key)
                || locked.Contains(query.Key)
                || !Forwardable.Contains(query.Key)
                || string.Equals(query.Key, ClickIdParameter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Set(parts.Query, Uri.EscapeDataString(query.Key), Uri.EscapeDataString(query.Value ?? string.Empty));
        }

        // 3. Our own click id, only with consent (FR-128, §E.6.2). It is written last and overwrites any
        //    value already present, because a stale dl_cid must never be mistaken for this click.
        if (consent.AllowClickIdLinking && !string.IsNullOrEmpty(clickId))
        {
            Set(parts.Query, ClickIdParameter, Uri.EscapeDataString(clickId));
        }

        return Render(parts);
    }

    /// <summary>
    /// Builds the URL behind the interstitial's <c>&lt;a&gt;</c> button, without consent information.
    /// </summary>
    /// <param name="decision">The routing decision.</param>
    /// <param name="link">The link snapshot, supplying the fallback deep link path.</param>
    /// <param name="customScheme">The application's custom URI scheme, for example <c>myapp</c>. May be <see langword="null"/>.</param>
    /// <param name="clickId">The click identifier for this request.</param>
    /// <returns>The deep link URL, or <see langword="null"/> when the link has no deep link target.</returns>
    /// <remarks>
    /// Consent defaults to denied, so this overload never emits a click-id parameter. Callers on the
    /// resolve path must use the overload that takes the decision produced by the consent gate.
    /// </remarks>
    public static string? BuildDeeplinkUrl(RoutingDecision decision, LinkSnapshot link, string? customScheme, string clickId) =>
        BuildDeeplinkUrl(decision, link, customScheme, clickId, NoConsent);

    /// <summary>
    /// Builds the URL behind the interstitial's <c>&lt;a&gt;</c> button: either a Universal/App Link
    /// (<c>https://host/path</c>) when the configured deep link is already an absolute web URL, or the
    /// application's custom scheme (<c>myapp://path</c>) as the last-resort fallback.
    /// </summary>
    /// <param name="decision">The routing decision.</param>
    /// <param name="link">The link snapshot, supplying the fallback deep link path.</param>
    /// <param name="customScheme">The application's custom URI scheme, for example <c>myapp</c>. May be <see langword="null"/>.</param>
    /// <param name="clickId">The click identifier for this request.</param>
    /// <param name="consent">The consent decision; it gates the click id, nothing else.</param>
    /// <returns>The deep link URL, or <see langword="null"/> when the link has no deep link target.</returns>
    /// <remarks>
    /// Any application may register the same custom scheme and no operating system verifies the claim
    /// (§A.2.3, CVE-2026-26123), so nothing but the click id is ever placed on a custom-scheme URL, and
    /// web schemes are refused as a "custom" scheme so that this cannot be turned into an open redirect.
    /// </remarks>
    public static string? BuildDeeplinkUrl(
        RoutingDecision decision,
        LinkSnapshot link,
        string? customScheme,
        string clickId,
        ConsentDecision consent)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(consent);

        string? path = decision.DeeplinkPath ?? link.DeeplinkPath;
        string? baseUrl = AbsoluteWebUrl(path);

        if (baseUrl is null)
        {
            string? scheme = NormalizeCustomScheme(customScheme);

            if (scheme is null)
            {
                return null;
            }

            string tail = (path ?? string.Empty).TrimStart('/');
            baseUrl = string.Concat(scheme, "://", tail);
        }

        UrlParts parts = Split(baseUrl);

        if (consent.AllowClickIdLinking && !string.IsNullOrEmpty(clickId))
        {
            Set(parts.Query, ClickIdParameter, Uri.EscapeDataString(clickId));
        }

        return Render(parts);
    }

    // ------------------------------------------------------------------ store URL decoration

    /// <summary>
    /// Expands the referrer template, encodes the whole result exactly once and installs it as the Play
    /// Store <c>referrer</c> parameter (§A.2.4). Encoding once — not per pair — is deliberate: Play hands
    /// the string back to the application verbatim after a single decode.
    /// </summary>
    private static void ApplyPlayReferrer(
        UrlParts parts,
        RoutingDecision decision,
        LinkSnapshot link,
        string clickId,
        ConsentDecision consent)
    {
        string template = string.IsNullOrWhiteSpace(decision.ReferrerTemplate)
            ? DefaultReferrerTemplate
            : decision.ReferrerTemplate;

        List<string> segments = ExpandReferrer(template, link, clickId, consent);

        if (segments.Count == 0)
        {
            return;
        }

        string encoded = Uri.EscapeDataString(string.Join('&', segments));

        // Trim from the tail until the encoded string fits, but never drop the click id: it is the only
        // deterministic attribution channel Android has, while the UTM pairs are duplicated in the click
        // stream anyway.
        while (encoded.Length > MaxEncodedReferrerLength)
        {
            int droppable = LastDroppableIndex(segments);

            if (droppable < 0)
            {
                break;
            }

            segments.RemoveAt(droppable);
            encoded = Uri.EscapeDataString(string.Join('&', segments));
        }

        Set(parts.Query, ReferrerParameter, encoded);
    }

    private static List<string> ExpandReferrer(
        string template,
        LinkSnapshot link,
        string clickId,
        ConsentDecision consent)
    {
        string effectiveClickId = consent.AllowClickIdLinking ? clickId ?? string.Empty : string.Empty;
        string[] rawSegments = template.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var expanded = new List<string>(rawSegments.Length);

        foreach (string rawSegment in rawSegments)
        {
            string segment = rawSegment
                .Replace("{click_id}", effectiveClickId, StringComparison.OrdinalIgnoreCase)
                .Replace("{utm_source}", Utm(link, UtmSourceKey), StringComparison.OrdinalIgnoreCase)
                .Replace("{utm_medium}", Utm(link, UtmMediumKey), StringComparison.OrdinalIgnoreCase)
                .Replace("{utm_campaign}", Utm(link, UtmCampaignKey), StringComparison.OrdinalIgnoreCase)
                .Replace("{utm_term}", Utm(link, UtmTermKey), StringComparison.OrdinalIgnoreCase)
                .Replace("{utm_content}", Utm(link, UtmContentKey), StringComparison.OrdinalIgnoreCase)
                .Replace("{link_id}", link.Id.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

            // A pair whose placeholder resolved to nothing — an unset UTM field, or the click id under a
            // consent mode that forbids linking — is dropped rather than sent as "utm_term=".
            int equals = segment.IndexOf('=');

            if (segment.Length == 0 || (equals >= 0 && equals == segment.Length - 1))
            {
                continue;
            }

            expanded.Add(segment);
        }

        return expanded;
    }

    private static int LastDroppableIndex(List<string> segments)
    {
        for (int i = segments.Count - 1; i >= 0; i--)
        {
            if (!IsClickIdSegment(segments[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsClickIdSegment(string segment)
    {
        int equals = segment.IndexOf('=');
        ReadOnlySpan<char> key = equals < 0 ? segment.AsSpan() : segment.AsSpan(0, equals);

        return key.Trim().Equals(ClickIdParameter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// App Store campaign links carry <c>pt</c> (provider), <c>ct</c> (campaign) and <c>mt</c> (media type).
    /// Whatever the operator already put on the store URL is authoritative; only a missing campaign token
    /// is filled in from the link's own UTM campaign (TC-101).
    /// </summary>
    private static void ApplyAppleCampaign(UrlParts parts, LinkSnapshot link)
    {
        if (IndexOfKey(parts.Query, AppleCampaignParameter) >= 0)
        {
            return;
        }

        string campaign = Utm(link, UtmCampaignKey);

        if (campaign.Length == 0)
        {
            return;
        }

        Set(parts.Query, AppleCampaignParameter, Uri.EscapeDataString(campaign));
    }

    private static string Utm(LinkSnapshot link, string key) =>
        link.Utm.TryGetValue(key, out string? value) && value is not null ? value : string.Empty;

    // ------------------------------------------------------------------ URL plumbing

    /// <summary>
    /// A URL split into the part that must never be touched (scheme, authority, path), the ordered query
    /// pairs, and the fragment. Keys and values are kept exactly as they arrived, already percent-encoded;
    /// anything added later is encoded on the way in, so nothing is ever encoded twice.
    /// </summary>
    private sealed record UrlParts(string Prefix, List<KeyValuePair<string, string?>> Query, string Fragment);

    private static UrlParts Split(string url)
    {
        string work = url;
        string fragment = string.Empty;

        int hash = work.IndexOf('#');

        if (hash >= 0)
        {
            fragment = work[hash..];
            work = work[..hash];
        }

        string prefix = work;
        var query = new List<KeyValuePair<string, string?>>();
        int question = work.IndexOf('?');

        if (question >= 0)
        {
            prefix = work[..question];

            foreach (string pair in work[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = pair.IndexOf('=');

                query.Add(equals < 0
                    ? new KeyValuePair<string, string?>(pair, null)
                    : new KeyValuePair<string, string?>(pair[..equals], pair[(equals + 1)..]));
            }
        }

        return new UrlParts(prefix, query, fragment);
    }

    private static string Render(UrlParts parts)
    {
        if (parts.Query.Count == 0)
        {
            return string.Concat(parts.Prefix, parts.Fragment);
        }

        var builder = new StringBuilder(parts.Prefix.Length + (parts.Query.Count * 24) + parts.Fragment.Length);
        builder.Append(parts.Prefix).Append('?');

        for (int i = 0; i < parts.Query.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('&');
            }

            builder.Append(parts.Query[i].Key);

            if (parts.Query[i].Value is { } value)
            {
                builder.Append('=').Append(value);
            }
        }

        return builder.Append(parts.Fragment).ToString();
    }

    private static void Set(List<KeyValuePair<string, string?>> query, string key, string encodedValue)
    {
        int index = IndexOfKey(query, key);
        var pair = new KeyValuePair<string, string?>(key, encodedValue);

        if (index < 0)
        {
            query.Add(pair);
        }
        else
        {
            query[index] = pair;
        }
    }

    private static int IndexOfKey(List<KeyValuePair<string, string?>> query, string key)
    {
        for (int i = 0; i < query.Count; i++)
        {
            if (string.Equals(query[i].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Returns the URL when it is an absolute <c>http</c>/<c>https</c> URL, otherwise <see langword="null"/>.</summary>
    private static string? AbsoluteWebUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        return uri.Scheme is "http" or "https" ? url : null;
    }

    /// <summary>
    /// Accepts a custom scheme only if it is a syntactically valid URI scheme that is neither a web
    /// scheme nor one of the schemes that can execute code in a browser (T-01).
    /// </summary>
    private static string? NormalizeCustomScheme(string? customScheme)
    {
        if (string.IsNullOrWhiteSpace(customScheme))
        {
            return null;
        }

        string scheme = customScheme.Trim().TrimEnd(':', '/');

        if (scheme.Length == 0 || !char.IsAsciiLetter(scheme[0]) || DeniedSchemes.Contains(scheme))
        {
            return null;
        }

        foreach (char c in scheme)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.'))
            {
                return null;
            }
        }

        return scheme;
    }
}
