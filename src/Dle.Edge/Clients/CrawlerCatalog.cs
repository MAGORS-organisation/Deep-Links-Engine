using System.Collections.Frozen;

namespace Dle.Edge.Clients;

/// <summary>
/// The crawlers the edge recognises by user agent, and how each of them can be confirmed
/// (FR-161, ADR-009, TC-106, TC-107).
/// </summary>
/// <remarks>
/// <para>
/// A social crawler needs an HTML document with Open Graph tags, not a redirect: it will not follow
/// a 30x reliably and it does not run script, so a redirected preview comes out blank (§A.2.6). That
/// makes crawler detection a routing decision rather than a statistic, which is why the table is
/// explicit rather than a heuristic over the word "bot".
/// </para>
/// <para>
/// Only the search engines publish a reverse DNS convention, so only they can be confirmed. The
/// social fetchers — Facebook, X, Slack, LinkedIn, Discord, WhatsApp, Telegram — publish address
/// ranges instead, and downloading those would be an outbound third-party call, which NFR-14 rules
/// out on this path. They are therefore recognised but not confirmable, and that is a deliberate and
/// bounded acceptance: the worst a forged <c>facebookexternalhit</c> can obtain is the Open Graph
/// document that the link already publishes to anyone who shares it, and the click it produces is
/// marked <c>is_bot</c> so it never reaches a campaign report.
/// </para>
/// <para>
/// Order matters in <see cref="TryMatch"/>. Telegram's fetcher identifies itself as
/// <c>TelegramBot (like TwitterBot)</c>, so a naive scan for <c>Twitterbot</c> would attribute every
/// Telegram preview to X.
/// </para>
/// </remarks>
public static class CrawlerCatalog
{
    /// <summary>Canonical name of the Facebook link preview fetcher.</summary>
    public const string Facebook = "facebookexternalhit";

    /// <summary>Canonical name of the X link preview fetcher.</summary>
    public const string Twitter = "Twitterbot";

    /// <summary>Canonical name of the Slack link preview fetcher.</summary>
    public const string Slack = "Slackbot";

    /// <summary>Canonical name of the LinkedIn link preview fetcher.</summary>
    public const string LinkedIn = "LinkedInBot";

    /// <summary>Canonical name of the Discord link preview fetcher.</summary>
    public const string Discord = "Discordbot";

    /// <summary>Canonical name of the WhatsApp link preview fetcher.</summary>
    public const string WhatsApp = "WhatsApp";

    /// <summary>Canonical name of the Telegram link preview fetcher.</summary>
    public const string Telegram = "TelegramBot";

    /// <summary>Canonical name of the Google crawler family.</summary>
    public const string Google = "Googlebot";

    /// <summary>Canonical name of the Bing crawler family.</summary>
    public const string Bing = "bingbot";

    /// <summary>Canonical name of the Apple crawler.</summary>
    public const string Apple = "Applebot";

    private static readonly string[] GoogleSuffixes = [".googlebot.com", ".google.com", ".googleusercontent.com"];
    private static readonly string[] BingSuffixes = [".search.msn.com"];
    private static readonly string[] AppleSuffixes = [".applebot.apple.com", ".apple.com"];
    private static readonly string[] NoSuffixes = [];

    // Scanned in order. Every entry is a case-insensitive substring of the user agent, which is how
    // these fetchers are identified in practice: none of them exposes a structured client hint.
    private static readonly (string Token, string Name)[] Tokens =
    [
        // Before Twitterbot: the Telegram fetcher spells itself "TelegramBot (like TwitterBot)".
        ("TelegramBot", Telegram),
        ("facebookexternalhit", Facebook),
        ("facebookcatalog", Facebook),
        ("Twitterbot", Twitter),
        ("Slackbot", Slack),
        ("Slack-ImgProxy", Slack),
        ("LinkedInBot", LinkedIn),
        ("Discordbot", Discord),
        ("Googlebot", Google),
        ("Google-InspectionTool", Google),
        ("AdsBot-Google", Google),
        ("Storebot-Google", Google),
        ("bingbot", Bing),
        ("BingPreview", Bing),
        ("Applebot", Apple),
    ];

    private static readonly FrozenDictionary<string, string[]> Verification =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [Google] = GoogleSuffixes,
            [Bing] = BingSuffixes,
            [Apple] = AppleSuffixes,
            [Facebook] = NoSuffixes,
            [Twitter] = NoSuffixes,
            [Slack] = NoSuffixes,
            [LinkedIn] = NoSuffixes,
            [Discord] = NoSuffixes,
            [WhatsApp] = NoSuffixes,
            [Telegram] = NoSuffixes,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Identifies the crawler a user agent claims to be.
    /// </summary>
    /// <param name="userAgent">Raw user agent header value.</param>
    /// <param name="name">Canonical crawler name when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the user agent claims to be a known crawler.</returns>
    /// <remarks>
    /// WhatsApp is the one ambiguous token. Its preview fetcher sends a bare <c>WhatsApp/2.x</c>
    /// while its in-app browser sends a full browser string; treating both as a crawler would serve
    /// a preview page to a real person who tapped the link inside a chat.
    /// </remarks>
    public static bool TryMatch(string? userAgent, out string name)
    {
        name = string.Empty;

        if (string.IsNullOrEmpty(userAgent))
        {
            return false;
        }

        foreach ((string token, string canonical) in Tokens)
        {
            if (userAgent.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                name = canonical;
                return true;
            }
        }

        if (userAgent.Contains(WhatsApp, StringComparison.OrdinalIgnoreCase) &&
            !userAgent.Contains("Mozilla", StringComparison.OrdinalIgnoreCase))
        {
            name = WhatsApp;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the reverse DNS suffixes that confirm a crawler.
    /// </summary>
    /// <param name="crawlerName">Canonical crawler name from <see cref="TryMatch"/>.</param>
    /// <param name="suffixes">
    /// The permitted host name suffixes. An empty array means this crawler publishes no reverse DNS
    /// convention and therefore cannot be confirmed by DNS at all.
    /// </param>
    /// <returns><see langword="false"/> when the name is not in the catalogue.</returns>
    public static bool TryGetVerificationSuffixes(string? crawlerName, out string[] suffixes)
    {
        if (crawlerName is not null && Verification.TryGetValue(crawlerName, out string[]? found))
        {
            suffixes = found;
            return true;
        }

        suffixes = NoSuffixes;
        return false;
    }
}
