namespace Dle.Edge.Rendering;

/// <summary>
/// What the interstitial's secondary anchor leads to. It selects the anchor's label and nothing else.
/// </summary>
/// <remarks>
/// The label matters more than it looks. "Continue" tells the user nothing, and a user who does not
/// know that the next tap opens the App Store is a user who abandons the flow there. The classification
/// is made from the URL the server itself built, never from anything the request supplied.
/// </remarks>
internal enum FallbackTarget
{
    /// <summary>An ordinary web page: the link's own target or the rule's web URL.</summary>
    Website = 0,

    /// <summary>Apple's App Store.</summary>
    AppStore = 1,

    /// <summary>Google Play.</summary>
    GooglePlay = 2,

    /// <summary>A store that is neither of the two known ones, for example a regional Android store.</summary>
    OtherStore = 3,
}
