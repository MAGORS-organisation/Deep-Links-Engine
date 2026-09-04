namespace Dle.Domain.Clients;

/// <summary>
/// The surface the request came from. In app browsers get their own values because they decide
/// whether a universal link can open the target application at all, which is the single most
/// important routing input after the platform.
/// </summary>
/// <remarks>
/// The numbering is grouped on purpose and must stay stable: values below 10 are ordinary web
/// clients, 10 to 29 are in app browsers, 30 and above are native applications talking to the API.
/// </remarks>
public enum ClientChannel
{
    /// <summary>The channel could not be determined.</summary>
    Unknown = 0,

    /// <summary>A regular standalone browser.</summary>
    Browser = 1,

    /// <summary>A crawler or link preview fetcher, served the Open Graph page.</summary>
    Crawler = 2,

    /// <summary>The Facebook in app browser.</summary>
    InAppFacebook = 10,

    /// <summary>The Instagram in app browser.</summary>
    InAppInstagram = 11,

    /// <summary>The TikTok in app browser.</summary>
    InAppTikTok = 12,

    /// <summary>The LinkedIn in app browser.</summary>
    InAppLinkedIn = 13,

    /// <summary>The Snapchat in app browser.</summary>
    InAppSnapchat = 14,

    /// <summary>The X (Twitter) in app browser.</summary>
    InAppTwitter = 15,

    /// <summary>The WhatsApp in app browser.</summary>
    InAppWhatsApp = 16,

    /// <summary>The Telegram in app browser.</summary>
    InAppTelegram = 17,

    /// <summary>The Pinterest in app browser.</summary>
    InAppPinterest = 18,

    /// <summary>An in app browser that is recognised as such but not identified further.</summary>
    InAppGeneric = 19,

    /// <summary>A native application using the SDK, not a browser.</summary>
    NativeApp = 30,
}
