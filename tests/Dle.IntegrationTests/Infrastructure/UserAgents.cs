namespace Dle.IntegrationTests.Infrastructure;

/// <summary>
/// The user agent strings the resolve tests classify against.
/// </summary>
/// <remarks>
/// Real strings, copied from real clients. A synthetic one would exercise the classifier's fallback
/// rather than its table, and the fallback is not what any of these tests is about.
/// </remarks>
public static class UserAgents
{
    /// <summary>Safari on iOS 18, the plain tap-a-link case of the device matrix row 1.</summary>
    public const string IosSafari =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 "
        + "(KHTML, like Gecko) Version/18.1 Mobile/15E148 Safari/604.1";

    /// <summary>Chrome on Android 15, device matrix row 5.</summary>
    public const string AndroidChrome =
        "Mozilla/5.0 (Linux; Android 15; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) "
        + "Chrome/131.0.0.0 Mobile Safari/537.36";

    /// <summary>Chrome on Windows, device matrix row 8: web fallback, no attempt to open an app.</summary>
    public const string DesktopChrome =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
        + "Chrome/131.0.0.0 Safari/537.36";

    /// <summary>Facebook's link preview fetcher (TC-106).</summary>
    public const string FacebookExternalHit =
        "facebookexternalhit/1.1 (+http://www.facebook.com/externalhit_uatext.php)";

    /// <summary>Instagram's in-app browser, device matrix row 3.</summary>
    public const string InstagramInApp =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 "
        + "(KHTML, like Gecko) Mobile/22B83 Instagram 360.0.0.30.98 (iPhone16,2; iOS 18_1; en_US)";
}
