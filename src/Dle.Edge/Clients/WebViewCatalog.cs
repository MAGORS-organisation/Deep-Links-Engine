namespace Dle.Edge.Clients;

/// <summary>
/// Maps a user agent to the in-app browser it came from (§A.2.6, FR-162).
/// </summary>
/// <remarks>
/// <para>
/// This is the detection that decides whether a client gets a redirect or an interstitial, and
/// getting it wrong is expensive in both directions. A missed webview means the universal link never
/// fires, because a webview only hands a link to the operating system on a real tap and a scripted
/// navigation is not a user gesture; a false positive means an ordinary browser is shown a page with
/// a button when it could have been redirected.
/// </para>
/// <para>
/// The tokens are the ones the applications actually send. Facebook and Instagram share a family of
/// <c>FB…</c> tokens, so Instagram is checked first: its webview sends both <c>Instagram</c> and
/// <c>FBAV</c>.
/// </para>
/// </remarks>
public static class WebViewCatalog
{
    /// <summary>User agent token an official DLE mobile SDK appends to identify itself.</summary>
    public const string SdkToken = "DleSdk/";

    // Order matters: Instagram's webview also carries the Facebook application tokens.
    private static readonly (string Token, ClientChannel Channel)[] Tokens =
    [
        ("Instagram", ClientChannel.InAppInstagram),
        ("FBAN", ClientChannel.InAppFacebook),
        ("FBAV", ClientChannel.InAppFacebook),
        ("FB_IAB", ClientChannel.InAppFacebook),
        ("FBIOS", ClientChannel.InAppFacebook),
        ("FB4A", ClientChannel.InAppFacebook),
        ("BytedanceWebview", ClientChannel.InAppTikTok),
        ("musical_ly", ClientChannel.InAppTikTok),
        ("TikTok", ClientChannel.InAppTikTok),
        ("trill", ClientChannel.InAppTikTok),
        ("LinkedInApp", ClientChannel.InAppLinkedIn),
        ("LinkedIn", ClientChannel.InAppLinkedIn),
        ("Snapchat", ClientChannel.InAppSnapchat),
        ("TwitterAndroid", ClientChannel.InAppTwitter),
        ("Twitter for iPhone", ClientChannel.InAppTwitter),
        ("WhatsApp", ClientChannel.InAppWhatsApp),
        ("Telegram", ClientChannel.InAppTelegram),
        ("Pinterest", ClientChannel.InAppPinterest),
        ("Line/", ClientChannel.InAppGeneric),
        ("KAKAOTALK", ClientChannel.InAppGeneric),
        ("MicroMessenger", ClientChannel.InAppGeneric),
    ];

    /// <summary>
    /// Classifies the channel a browser-shaped user agent came through.
    /// </summary>
    /// <param name="userAgent">Raw user agent header value.</param>
    /// <returns>
    /// The matching in-app channel, <see cref="ClientChannel.NativeApp"/> for a DLE SDK,
    /// <see cref="ClientChannel.Browser"/> for an ordinary browser, or
    /// <see cref="ClientChannel.Unknown"/> when there is no user agent at all.
    /// </returns>
    public static ClientChannel Classify(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent))
        {
            return ClientChannel.Unknown;
        }

        if (userAgent.Contains(SdkToken, StringComparison.OrdinalIgnoreCase))
        {
            return ClientChannel.NativeApp;
        }

        foreach ((string token, ClientChannel channel) in Tokens)
        {
            if (userAgent.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return channel;
            }
        }

        return IsUnbrandedWebView(userAgent) ? ClientChannel.InAppGeneric : ClientChannel.Browser;
    }

    /// <summary>
    /// Extracts the application version an SDK appended as <c>DleSdk/1.2.3</c>.
    /// </summary>
    /// <param name="userAgent">Raw user agent header value.</param>
    /// <returns>The version, or <see langword="null"/> when the token is absent or malformed.</returns>
    public static string? ReadSdkVersion(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent))
        {
            return null;
        }

        int start = userAgent.IndexOf(SdkToken, StringComparison.OrdinalIgnoreCase);

        if (start < 0)
        {
            return null;
        }

        ReadOnlySpan<char> tail = userAgent.AsSpan(start + SdkToken.Length);
        int length = 0;

        while (length < tail.Length && (char.IsAsciiDigit(tail[length]) || tail[length] is '.'))
        {
            length++;
        }

        return length == 0 ? null : new string(tail[..length]);
    }

    /// <summary>
    /// Recognises a webview that does not brand itself.
    /// </summary>
    /// <remarks>
    /// Android marks every <c>WebView</c> with the <c>; wv)</c> token, which is authoritative. On
    /// iOS there is no such marker, and the only usable signal is that <c>WKWebView</c> omits the
    /// <c>Safari</c> product token that mobile Safari always sends — so a string with
    /// <c>AppleWebKit</c> and <c>Mobile/</c> but no <c>Safari</c> is a webview inside some
    /// application. It is a heuristic, and it errs towards showing a page with a real button, which
    /// is the failure that still works.
    /// </remarks>
    private static bool IsUnbrandedWebView(string userAgent)
    {
        if (userAgent.Contains("; wv)", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return userAgent.Contains("AppleWebKit", StringComparison.OrdinalIgnoreCase)
            && userAgent.Contains("Mobile/", StringComparison.OrdinalIgnoreCase)
            && !userAgent.Contains("Safari", StringComparison.OrdinalIgnoreCase);
    }
}
