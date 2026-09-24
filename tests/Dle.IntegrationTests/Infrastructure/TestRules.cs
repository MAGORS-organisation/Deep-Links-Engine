using System.Text.Json;

using Dle.Domain.Routing;
using Dle.Domain.Serialization;

namespace Dle.IntegrationTests.Infrastructure;

/// <summary>
/// Routing rule sets used by the seeded links, rendered through the product's own serializer.
/// </summary>
/// <remarks>
/// The JSON is produced by <see cref="DleDomainJsonContext"/> rather than written out by hand,
/// because that is exactly the contract the hot path reads back: <c>DapperLinkStore</c> deserializes
/// <c>links.routing_rules</c> with the same source generated metadata. A hand-written document that
/// happened to differ in a property name would make every routing assertion in this suite a test of
/// the test data.
/// </remarks>
public static class TestRules
{
    /// <summary>Renders a rule set as the <c>links.routing_rules</c> document.</summary>
    /// <param name="rules">The rules, default last (FR-127).</param>
    /// <returns>The JSON document.</returns>
    public static string ToJson(params RoutingRule[] rules) =>
        JsonSerializer.Serialize(rules, DleDomainJsonContext.Default.RoutingRuleArray);

    /// <summary>The minimum valid rule set: a single default rule serving the web fallback.</summary>
    /// <returns>The JSON document.</returns>
    public static string WebDefault() => ToJson(DefaultWeb());

    /// <summary>
    /// The set TC-101 needs: iOS goes to the App Store, everything else falls through to the web.
    /// </summary>
    /// <param name="storeUrl">The App Store URL, carrying its <c>pt</c> and <c>ct</c> parameters.</param>
    /// <param name="deeplinkPath">Path handed to the application when it is installed.</param>
    /// <param name="interstitial">Interstitial policy for the iOS rule.</param>
    /// <returns>The JSON document.</returns>
    public static string IosAppOrStore(
        string storeUrl,
        string? deeplinkPath = null,
        InterstitialMode interstitial = InterstitialMode.Auto) =>
        ToJson(
            new RoutingRule
            {
                Id = "ios",
                When = new RuleCondition { Platform = ["ios"] },
                Then = new RuleAction
                {
                    Action = RoutingActionKind.AppOrStore,
                    StoreUrl = storeUrl,
                    DeeplinkPath = deeplinkPath,
                    Interstitial = interstitial,
                },
            },
            DefaultWeb());

    /// <summary>The default rule every stored set has to end with (FR-127, TC-105).</summary>
    /// <param name="url">
    /// The web target. Left null the engine falls back to <c>LinkSnapshot.TargetUrl</c>, which is
    /// what a rule set written without an explicit URL relies on; the control plane's validator
    /// insists on one, so a set created through the API passes it.
    /// </param>
    /// <returns>The rule.</returns>
    public static RoutingRule DefaultWeb(string? url = null) => new()
    {
        Id = "default",
        When = null,
        Then = new RuleAction { Action = RoutingActionKind.Web, Url = url },
    };
}
