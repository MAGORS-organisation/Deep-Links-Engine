using Dle.Domain.Clients;
using Dle.Domain.Routing;

namespace Dle.Domain.Analytics;

/// <summary>
/// Canonical values of the <c>click_events.decision</c> column (specification §B.5.3).
/// The wire and storage representation of <see cref="DecisionKind"/> is a stable lowercase
/// string so that the column stays readable in the database and survives enum renumbering.
/// </summary>
public static class DecisionNames
{
    /// <summary>The application was opened directly through a universal or app link.</summary>
    public const string AppOpen = "app_open";

    /// <summary>The client was sent to the Apple App Store.</summary>
    public const string StoreIos = "store_ios";

    /// <summary>The client was sent to Google Play.</summary>
    public const string StoreAndroid = "store_android";

    /// <summary>The client was sent to the web fallback target.</summary>
    public const string Web = "web";

    /// <summary>The client received the interstitial page with a real tap target.</summary>
    public const string Interstitial = "interstitial";

    /// <summary>A crawler received the Open Graph preview page.</summary>
    public const string Preview = "preview";

    /// <summary>The request was blocked by a routing rule.</summary>
    public const string Blocked = "blocked";

    /// <summary>No link matched the requested host and slug.</summary>
    public const string NotFound = "not_found";

    /// <summary>The link exists but is quarantined and therefore permanently gone.</summary>
    public const string Gone = "gone";

    /// <summary>
    /// Maps a routing decision to the stored decision name.
    /// <see cref="DecisionKind.Store"/> is platform dependent: it becomes
    /// <see cref="StoreIos"/> on iOS and <see cref="StoreAndroid"/> on Android; on any other
    /// platform there is no store to send the client to, so the decision degrades to
    /// <see cref="Web"/>. Every other kind maps to its own name.
    /// </summary>
    /// <param name="kind">The routing decision class produced by the routing engine.</param>
    /// <param name="platform">The classified platform of the client.</param>
    /// <returns>The stable decision name written to the click stream.</returns>
    public static string From(DecisionKind kind, Platform platform) => kind switch
    {
        DecisionKind.Web => Web,
        DecisionKind.Store => platform switch
        {
            Platform.Ios => StoreIos,
            Platform.Android => StoreAndroid,
            _ => Web,
        },
        DecisionKind.AppDirect => AppOpen,
        DecisionKind.Interstitial => Interstitial,
        DecisionKind.Blocked => Blocked,
        DecisionKind.NotFound => NotFound,
        DecisionKind.Gone => Gone,
        DecisionKind.Preview => Preview,
        _ => Web,
    };
}
