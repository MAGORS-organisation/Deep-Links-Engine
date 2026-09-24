using System.Collections.ObjectModel;
using System.Globalization;
using Dle.Domain.Clients;
using Dle.Domain.Privacy;
using Dle.Domain.Routing;

namespace Dle.UnitTests.Routing;

/// <summary>
/// A clock that never moves. SHARED-KERNEL section 17.2 forbids DateTime.UtcNow; a test that reads
/// the wall clock is the same defect wearing a different hat, so every routing test either supplies
/// ClientContext.ReceivedAt explicitly or injects this.
/// </summary>
internal sealed class FrozenClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal static class TestClients
{
    /// <summary>Saturday 2026-03-14 12:30 UTC. Hour 12, day of week 6.</summary>
    public static readonly DateTimeOffset Now = new(2026, 3, 14, 12, 30, 0, TimeSpan.Zero);

    public static ClientContext Client(
        Platform platform = Platform.Ios,
        ClientChannel channel = ClientChannel.Browser,
        string? country = null,
        string? region = null,
        string? language = null,
        string? osVersion = null,
        string? appVersion = null,
        DateTimeOffset? receivedAt = null,
        IReadOnlyDictionary<string, string>? query = null) => new()
        {
            Platform = platform,
            DeviceClass = DeviceClass.Phone,
            Channel = channel,
            Country = country,
            Region = region,
            Language = language,
            OsVersion = osVersion,
            AppVersion = appVersion,
            ReceivedAt = receivedAt ?? Now,
            Query = query ?? ReadOnlyDictionary<string, string>.Empty,
        };

    public static ConsentDecision FullConsent { get; } =
        ConsentGate.Evaluate(ConsentMode.Full, null, new ConsentSignal { Attribution = true, Analytics = true });

    public static ConsentDecision NoConsent { get; } = ConsentDecision.Denied("test_no_consent");

    public static RoutingRule Rule(string id, RuleCondition? when, RuleAction then) =>
        new() { Id = id, When = when, Then = then };

    public static RoutingRule DefaultRule(string id = "default", string url = "https://fallback.example.com/") =>
        new() { Id = id, When = null, Then = Web(url) };

    public static RuleAction Web(string url) => new() { Action = RoutingActionKind.Web, Url = url };

    public static RuleAction Store(string storeUrl, InterstitialMode interstitial = InterstitialMode.Auto) =>
        new() { Action = RoutingActionKind.AppOrStore, StoreUrl = storeUrl, Interstitial = interstitial };

    /// <summary>
    /// Finds a click id whose ConsistentBucket is exactly the requested value. The search is a
    /// deterministic scan over a fixed sequence, so the value is stable across runs, machines and
    /// framework versions, unlike anything drawn from a random source.
    /// </summary>
    public static string ClickIdWithBucket(int bucket)
    {
        for (int i = 0; i < 200_000; i++)
        {
            string candidate = string.Create(CultureInfo.InvariantCulture, $"click-{i}");
            if (ConsistentBucket.Of(candidate) == bucket)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            string.Create(CultureInfo.InvariantCulture, $"No click id in the scanned range hashes to bucket {bucket}."));
    }
}
