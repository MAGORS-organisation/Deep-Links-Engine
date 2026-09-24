namespace Dle.Domain.Routing;

/// <summary>
/// Canonical channel names shared by the JSON rule format and the click stream.
/// </summary>
/// <remarks>
/// The names are part of the public wire contract: they appear in <c>routing_rules</c> authored by
/// customers and in the <c>channel</c> column of <c>click_events</c>. They must never change, because
/// a rename would silently retarget every stored rule and orphan every historical report.
/// </remarks>
public static class ChannelNames
{
    /// <summary>The client could not be classified.</summary>
    public const string Unknown = "unknown";

    /// <summary>An ordinary web browser, not embedded in another application.</summary>
    public const string Browser = "browser";

    /// <summary>A verified crawler; answered with an Open Graph preview rather than a redirect (FR-161, ADR-009).</summary>
    public const string Crawler = "crawler";

    /// <summary>Facebook in-app webview.</summary>
    public const string InAppFacebook = "in_app_fb";

    /// <summary>Instagram in-app webview.</summary>
    public const string InAppInstagram = "in_app_ig";

    /// <summary>TikTok in-app webview.</summary>
    public const string InAppTikTok = "in_app_tiktok";

    /// <summary>LinkedIn in-app webview.</summary>
    public const string InAppLinkedIn = "in_app_linkedin";

    /// <summary>Snapchat in-app webview.</summary>
    public const string InAppSnapchat = "in_app_snapchat";

    /// <summary>X (formerly Twitter) in-app webview.</summary>
    public const string InAppTwitter = "in_app_x";

    /// <summary>WhatsApp in-app webview.</summary>
    public const string InAppWhatsApp = "in_app_whatsapp";

    /// <summary>Telegram in-app webview.</summary>
    public const string InAppTelegram = "in_app_telegram";

    /// <summary>Pinterest in-app webview.</summary>
    public const string InAppPinterest = "in_app_pinterest";

    /// <summary>An in-app webview that could be recognised as such but not attributed to a known application.</summary>
    public const string InAppGeneric = "in_app_other";

    /// <summary>A native application calling through the SDK rather than a browser.</summary>
    public const string NativeApp = "app";

    /// <summary>Maps a classified channel to its canonical name.</summary>
    /// <param name="channel">The channel produced by the client classifier.</param>
    /// <returns>The canonical name; <see cref="Unknown"/> for any value that is not a defined member.</returns>
    public static string From(ClientChannel channel) => channel switch
    {
        ClientChannel.Browser => Browser,
        ClientChannel.Crawler => Crawler,
        ClientChannel.InAppFacebook => InAppFacebook,
        ClientChannel.InAppInstagram => InAppInstagram,
        ClientChannel.InAppTikTok => InAppTikTok,
        ClientChannel.InAppLinkedIn => InAppLinkedIn,
        ClientChannel.InAppSnapchat => InAppSnapchat,
        ClientChannel.InAppTwitter => InAppTwitter,
        ClientChannel.InAppWhatsApp => InAppWhatsApp,
        ClientChannel.InAppTelegram => InAppTelegram,
        ClientChannel.InAppPinterest => InAppPinterest,
        ClientChannel.InAppGeneric => InAppGeneric,
        ClientChannel.NativeApp => NativeApp,
        _ => Unknown,
    };

    /// <summary>Maps a canonical name back to a channel.</summary>
    /// <param name="name">A canonical name; comparison ignores case and surrounding whitespace.</param>
    /// <returns>The matching channel, or <see cref="ClientChannel.Unknown"/> for an unrecognised name.</returns>
    public static ClientChannel Parse(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ClientChannel.Unknown;
        }

        string trimmed = name.Trim();

        return
            Is(trimmed, Browser) ? ClientChannel.Browser :
            Is(trimmed, Crawler) ? ClientChannel.Crawler :
            Is(trimmed, InAppFacebook) ? ClientChannel.InAppFacebook :
            Is(trimmed, InAppInstagram) ? ClientChannel.InAppInstagram :
            Is(trimmed, InAppTikTok) ? ClientChannel.InAppTikTok :
            Is(trimmed, InAppLinkedIn) ? ClientChannel.InAppLinkedIn :
            Is(trimmed, InAppSnapchat) ? ClientChannel.InAppSnapchat :
            Is(trimmed, InAppTwitter) ? ClientChannel.InAppTwitter :
            Is(trimmed, InAppWhatsApp) ? ClientChannel.InAppWhatsApp :
            Is(trimmed, InAppTelegram) ? ClientChannel.InAppTelegram :
            Is(trimmed, InAppPinterest) ? ClientChannel.InAppPinterest :
            Is(trimmed, InAppGeneric) ? ClientChannel.InAppGeneric :
            Is(trimmed, NativeApp) ? ClientChannel.NativeApp :
            ClientChannel.Unknown;
    }

    /// <summary>
    /// True for the channels that render inside another application's webview.
    /// </summary>
    /// <remarks>
    /// This is the single most consequential classification in the whole engine: such a webview hands a
    /// Universal Link or an App Link to the operating system only on a genuine tap of an <c>&lt;a&gt;</c>
    /// element, so these clients must be served an interstitial page rather than a redirect
    /// (§A.2.6, ADR-009, FR-162) — even when the rule asks for <see cref="InterstitialMode.Never"/>.
    /// </remarks>
    /// <param name="channel">The channel produced by the client classifier.</param>
    /// <returns><see langword="true"/> when the channel is an in-app webview.</returns>
    public static bool IsInAppWebView(ClientChannel channel) => channel switch
    {
        ClientChannel.InAppFacebook => true,
        ClientChannel.InAppInstagram => true,
        ClientChannel.InAppTikTok => true,
        ClientChannel.InAppLinkedIn => true,
        ClientChannel.InAppSnapchat => true,
        ClientChannel.InAppTwitter => true,
        ClientChannel.InAppWhatsApp => true,
        ClientChannel.InAppTelegram => true,
        ClientChannel.InAppPinterest => true,
        ClientChannel.InAppGeneric => true,
        _ => false,
    };

    private static bool Is(string value, string canonical) =>
        string.Equals(value, canonical, StringComparison.OrdinalIgnoreCase);
}
